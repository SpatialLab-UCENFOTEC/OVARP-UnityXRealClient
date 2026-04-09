// WebSocket client for the OVARP server (/ws/client/{id}); OpenAI fallback if unreachable at connect.
// Fires OnTextReply and OnTtsComplete regardless of backend — Controller stays transport-agnostic.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class OvarpServerConnector : MonoBehaviour
{
    [Header("OVARP server")]
    [SerializeField] private string editorServerUrl = "ws://localhost:8000";
    [SerializeField] private string questServerUrl  = "ws://192.168.x.x:8000";
    // Set at runtime by ServerSetupUI; overrides serialized URLs when non-empty
    private string _runtimeServerUrl;
    // client_id: identifies this connection in the WebSocket URL path
    [SerializeField] private string clientId    = "quest_vr_01";
    // sender: identifies the origin device in every message body
    [SerializeField] private string sender      = "quest_vr_01";
    [SerializeField] private string targetAgent = "agent_alpha";
    [SerializeField] private float  connectTimeoutSeconds = 3f;

    [Header("OpenAI fallback")]
    [SerializeField] private string openAIApiKey;

    // Fired on main thread
    public event Action            OnConnected;
    public event Action<string>    OnTextReply;
    public event Action<string>    OnUserTranscript;
    public event Action<AudioClip> OnTtsComplete;
    public event Action<string>    OnMovementCommand;   // move_closer, move_farther, move_left, move_right, reset_position
    public event Action<string>    OnAnimationCommand;  // clap, bow, thumbs_up, thinking, shrug, dance, etc.
    public event Action<string>    OnAvatarCommand;     // default, male_casual, female_formal, robot
    public event Action<string>    OnEmotionCommand;    // neutral, happy, sad, angry, surprised
    public event Action<string>    OnLooksCommand;      // user, away, agent_beta

    // ── private state ──────────────────────────────────────────────────────────
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private readonly List<byte> _ttsBuffer = new();   // accumulates decoded tts_chunk bytes
    private readonly SemaphoreSlim  _sendLock  = new(1, 1);
    private bool _fallbackMode   = false;
    private bool _isConnecting   = false;
    private float _reconnectDelay = 1f;

    // OpenAI fallback conversation history
    private readonly List<string> _messages = new();

    // ── serialization helpers (fallback only) ─────────────────────────────────
    [Serializable] private class Message      { public string text; }
    [Serializable] private class ChatCompletion { public Choice[] choices; }
    [Serializable] private class Choice       { public ChatGPTMessage message; }
    [Serializable] private class ChatGPTMessage { public string role; public string content; }

    // ══════════════════════════════════════════════════════════════════════════
    // Unity lifecycle
    // ══════════════════════════════════════════════════════════════════════════

    private void Start()
    {
        _cts = new CancellationTokenSource();
        // Connection is deferred to ServerSetupUI — call ConnectToServer() to initiate.
    }

    /// <summary>Called by ServerSetupUI with the user-entered server base URL (e.g. ws://192.168.1.5:8000).</summary>
    public void ConnectToServer(string serverBaseUrl)
    {
        _runtimeServerUrl = serverBaseUrl;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _fallbackMode = false;
        _ = TryConnectAsync();
    }

    private void Update()
    {
        while (_mainThreadQueue.TryDequeue(out Action action))
            action();
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _ws?.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Connection
    // ══════════════════════════════════════════════════════════════════════════

    private string ResolveUrl()
    {
        string base_ = !string.IsNullOrEmpty(_runtimeServerUrl)
            ? _runtimeServerUrl
            : (Application.platform == RuntimePlatform.Android ? questServerUrl : editorServerUrl);
        return $"{base_}/ws/client/{clientId}";
    }

    private async Task TryConnectAsync()
    {
        if (_isConnecting) return;
        _isConnecting = true;

        _ws?.Dispose();
        _ws = new ClientWebSocket();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(connectTimeoutSeconds));
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

        try
        {
            await _ws.ConnectAsync(new Uri(ResolveUrl()), linked.Token);
            _fallbackMode   = false;
            _reconnectDelay = 1f;
            Debug.Log("[OvarpServerConnector] Connected to OVARP server.");
            _mainThreadQueue.Enqueue(() => OnConnected?.Invoke());
            _ = ReceiveLoopAsync();
        }
        catch (Exception e)
        {
            if (!_fallbackMode)
            {
                _fallbackMode = true;
                Debug.LogWarning($"[OvarpServerConnector] Cannot reach OVARP server ({e.Message}). Using OpenAI fallback.");
                InitFallbackHistory();
            }
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void ScheduleReconnect()
    {
        StartCoroutine(ReconnectAfterDelay(_reconnectDelay));
        _reconnectDelay = Mathf.Min(_reconnectDelay * 2f, 30f);
    }

    private IEnumerator ReconnectAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        _ = TryConnectAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Public API
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Send recorded WAV bytes. Routes to OVARP WebSocket or OpenAI fallback.</summary>
    public void SendAudio(byte[] wavBytes)
    {
        if (_fallbackMode)
        {
            StartCoroutine(FallbackPipeline(wavBytes));
            return;
        }

        if (_ws?.State != WebSocketState.Open)
        {
            Debug.LogWarning("[OvarpServerConnector] WebSocket not open — dropping audio.");
            return;
        }

        _ = SendAudioAsync(wavBytes);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OVARP WebSocket path
    // ══════════════════════════════════════════════════════════════════════════

    private async Task SendAudioAsync(byte[] wavBytes)
    {
        string base64 = Convert.ToBase64String(wavBytes);
        string json = $"{{\"sender\":\"{sender}\",\"target_device\":\"all\","
                    + $"\"target_agent\":\"{targetAgent}\",\"command_type\":\"audio\","
                    + $"\"command\":\"stt_request\",\"subcommand\":{{\"audio_base64\":\"{base64}\"}}}}";

        byte[] encoded = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(encoded),
                                WebSocketMessageType.Text, true, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer  = new byte[32768];
        var builder = new StringBuilder();

        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Debug.LogWarning("[OvarpServerConnector] Server closed connection. Reconnecting…");
                    _mainThreadQueue.Enqueue(ScheduleReconnect);
                    return;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    string msg = builder.ToString();
                    HandleMessage(msg);
                    builder.Clear();
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception e)
        {
            Debug.LogWarning($"[OvarpServerConnector] Receive error: {e.Message}. Reconnecting…");
            _mainThreadQueue.Enqueue(ScheduleReconnect);
        }
    }

    // Runs on background thread — no Unity API calls allowed here
    private void HandleMessage(string json)
    {
        try
        {
            string topic   = ExtractField(json, "topic");
            string command = ExtractField(json, "command");

            if (topic == "message" && command == "user_transcript")
            {
                string text = ExtractField(json, "text");
                if (!string.IsNullOrEmpty(text))
                    _mainThreadQueue.Enqueue(() => OnUserTranscript?.Invoke(text));
            }
            else if (topic == "message" && command == "llm_reply")
            {
                string text = ExtractField(json, "text");
                if (!string.IsNullOrEmpty(text))
                    _mainThreadQueue.Enqueue(() => OnTextReply?.Invoke(text));
                else
                    Debug.LogWarning("[OvarpServerConnector] llm_reply received but text was empty.");
            }
            else if (topic == "audio" && command == "tts_chunk")
            {
                string chunk = ExtractField(json, "audio_base64");
                if (!string.IsNullOrEmpty(chunk))
                {
                    byte[] decoded = Convert.FromBase64String(chunk);
                    _ttsBuffer.AddRange(decoded);
                }
                else
                {
                    Debug.LogWarning("[OvarpServerConnector] tts_chunk received but audio_base64 was empty.");
                }
            }
            else if (topic == "audio" && command == "tts_complete")
            {
                if (_ttsBuffer.Count == 0)
                {
                    Debug.LogWarning("[OvarpServerConnector] tts_complete but buffer is empty.");
                    return;
                }
                byte[] wavBytes = _ttsBuffer.ToArray();
                _ttsBuffer.Clear();

                try
                {
                    var (samples, channels, sampleRate) = DecodeWavPcm(wavBytes);
                    _mainThreadQueue.Enqueue(() =>
                    {
                        var clip = AudioClip.Create("tts_ovarp", samples.Length / channels,
                                                    channels, sampleRate, false);
                        clip.SetData(samples, 0);
                        OnTtsComplete?.Invoke(clip);
                    });
                }
                catch (Exception e)
                {
                    Debug.LogError($"[OvarpServerConnector] WAV decode failed: {e.Message}");
                }
            }
            else if (topic == "action" && command == "execute_state")
            {
                string movement  = ExtractField(json, "movement");
                string animation = ExtractField(json, "actions");
                string avatar    = ExtractField(json, "avatar");
                string emotion   = ExtractField(json, "emotions");
                string looks     = ExtractField(json, "looks");

                if (!string.IsNullOrEmpty(movement))
                    _mainThreadQueue.Enqueue(() => OnMovementCommand?.Invoke(movement));
                else if (!string.IsNullOrEmpty(animation))
                    _mainThreadQueue.Enqueue(() => OnAnimationCommand?.Invoke(animation));
                else if (!string.IsNullOrEmpty(avatar))
                    _mainThreadQueue.Enqueue(() => OnAvatarCommand?.Invoke(avatar));
                else if (!string.IsNullOrEmpty(emotion))
                    _mainThreadQueue.Enqueue(() => OnEmotionCommand?.Invoke(emotion));
                else if (!string.IsNullOrEmpty(looks))
                    _mainThreadQueue.Enqueue(() => OnLooksCommand?.Invoke(looks));
                else
                    Debug.LogWarning($"[OvarpServerConnector] execute_state received but no recognized subcommand key.");
            }
            else
            {
                Debug.Log($"[OvarpServerConnector] Unhandled message — topic='{topic}' command='{command}'");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[OvarpServerConnector] HandleMessage error: {e.Message}");
        }
    }

    // Decodes assembled WAV bytes — runs on background thread (no Unity API)
    private static (float[] samples, int channels, int sampleRate) DecodeWavPcm(byte[] wav)
    {

        // Standard PCM WAV header layout (matches SavWav.cs output)
        int channels   = BitConverter.ToUInt16(wav, 22);
        int sampleRate = BitConverter.ToInt32(wav, 24);
        int dataOffset = 44; // SavWav always writes a 44-byte header

        int sampleCount = (wav.Length - dataOffset) / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
            samples[i] = BitConverter.ToInt16(wav, dataOffset + i * 2) / 32768f;

        return (samples, channels, sampleRate);
    }

    // Minimal JSON string-field extractor — handles both "key":"val" and "key": "val"
    private static string ExtractField(string json, string key)
    {
        string search = $"\"{key}\":";
        int start = json.IndexOf(search, StringComparison.Ordinal);
        if (start < 0) return null;
        start += search.Length;
        // skip optional whitespace
        while (start < json.Length && json[start] == ' ') start++;
        if (start >= json.Length || json[start] != '"') return null;
        start++; // skip opening quote
        int end = json.IndexOf('"', start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OpenAI fallback path
    // ══════════════════════════════════════════════════════════════════════════

    private void InitFallbackHistory()
    {
        _messages.Clear();
        string today = DateTime.Now.ToString("MM/dd/yyyy");
        _messages.Add($"{{\"role\":\"system\",\"content\":\"DateFormat: mm/dd/yyyy. Today: {today}.\"}}");
        _messages.Add("{\"role\":\"system\",\"content\":\"You are a trusted companion named Nova. Answer in 3 sentences or less. Be friendly, curious, and conversational. Never reveal you are not human.\"}");
    }

    private IEnumerator FallbackPipeline(byte[] wavBytes)
    {
        // 1 — Transcribe
        string transcript = null;
        yield return StartCoroutine(FallbackTranscribe(wavBytes, result => transcript = result));
        if (transcript == null) yield break;

        _messages.Add($"{{\"role\":\"user\",\"content\":\"{EscapeJson(transcript)}\"}}");
        OnTextReply?.Invoke(null); // signal transcript available (Controller shows it)

        // 2 — Chat
        string reply = null;
        yield return StartCoroutine(FallbackChat(result => reply = result));
        if (reply == null) yield break;

        _messages.Add($"{{\"role\":\"assistant\",\"content\":\"{EscapeJson(reply)}\"}}");
        OnTextReply?.Invoke(reply);

        // 3 — TTS
        yield return StartCoroutine(FallbackTTS(reply));
    }

    private IEnumerator FallbackTranscribe(byte[] wavBytes, Action<string> callback)
    {
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("model", "whisper-1"),
            new MultipartFormFileSection("file", wavBytes, "mic.wav", "audio/wav")
        };

        UnityWebRequest www = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form);
        www.SetRequestHeader("Authorization", "Bearer " + openAIApiKey);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[OvarpServerConnector] Whisper error: " + www.error);
            yield break;
        }

        var msg = JsonUtility.FromJson<Message>(www.downloadHandler.text);
        www.Dispose();
        callback(msg?.text);
    }

    private IEnumerator FallbackChat(Action<string> callback)
    {
        string body = $"{{\"model\":\"gpt-4\",\"messages\":[{string.Join(",", _messages)}]}}";
        byte[] raw  = Encoding.UTF8.GetBytes(body);

        int retries = 2;
        while (retries-- > 0)
        {
            UnityWebRequest www = UnityWebRequest.PostWwwForm("https://api.openai.com/v1/chat/completions", "");
            www.uploadHandler   = new UploadHandlerRaw(raw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Authorization", "Bearer " + openAIApiKey);
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                var completion = JsonUtility.FromJson<ChatCompletion>(www.downloadHandler.text);
                www.Dispose();
                callback(completion?.choices?[0]?.message?.content);
                yield break;
            }

            Debug.LogError("[OvarpServerConnector] GPT-4 error: " + www.error);
            www.Dispose();
        }
    }

    private IEnumerator FallbackTTS(string text)
    {
        string body = $"{{\"model\":\"tts-1\",\"input\":\"{EscapeJson(text)}\",\"voice\":\"shimmer\"}}";
        byte[] raw  = Encoding.UTF8.GetBytes(body);

        int retries = 2;
        while (retries-- > 0)
        {
            using UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(
                "https://api.openai.com/v1/audio/speech", AudioType.MPEG);
            www.method        = "POST";
            www.uploadHandler = new UploadHandlerRaw(raw);
            www.SetRequestHeader("Authorization", "Bearer " + openAIApiKey);
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                OnTtsComplete?.Invoke(DownloadHandlerAudioClip.GetContent(www));
                yield break;
            }

            Debug.LogError("[OvarpServerConnector] TTS error: " + www.error);
        }
    }

    private static string EscapeJson(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
