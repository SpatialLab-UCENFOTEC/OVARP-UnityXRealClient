using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.XR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
using System;
using System.IO;
using System.Text;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif



[RequireComponent(typeof(AudioSource))]
public class Controller : MonoBehaviour
{
    // WebGL-only JS interop — no-ops on Editor/Standalone
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void Hello(string param);

    [DllImport("__Internal")]
    private static extern void StoreEvents(string param);

    [DllImport("__Internal")]
    private static extern void Print(string param);

    [DllImport("__Internal")]
    private static extern void QuitGame();
#else
    private static void Hello(string param) { Debug.Log("[Hello] " + param); }
    private static void StoreEvents(string param) { Debug.Log("[StoreEvents] " + param); }
    private static void Print(string param) { Debug.Log("[Print] " + param); }
    private static void QuitGame() { Application.Quit(); }
#endif

    public AudioSource audioSource;
    public AudioSource audioSource2;
    public AgentState CurrentState { get; private set; }
    public string[] CurrentIntent { get; private set; }
    public string CurrentUtterance { get; private set; }
    private Queue<string> utteranceQueue = new Queue<string>();
    private Queue<string[]> intentQueue = new Queue<string[]>();
    public bool paused;
    public float audioFadeIn = .2f;
    public float audioFadeOut = .3f;
    public enum AgentState
    {
        Idle,
        Waiting,
        Speaking
    }

    public GameObject agent;
    public GameObject LipSync;
    public LipSync lipSync;

    private string outputFilePath;

    public Chat chat;
    public GameObject ChatObject;

    public Queue<string> responses;

    public AnimController anim;
    public RecIndicator recIndicator;
    public EmotionController emotionController;
    public HeadLookAt headLookAt;

    // OVARP WebSocket + OpenAI fallback (assigned in Inspector)
    public OvarpServerConnector serverConnector;

    private Vector3 _agentInitialPosition;
    private const float MoveStep = 0.1f;

    private bool isAudioReady = false;
    private bool isListening = false;
    private bool hasMicPermission = false;
    private bool _playbackStarted = false; // guards against exiting Speaking before Play() is called

    void Start()
    {
        StartCoroutine(RequestMicrophonePermission());

        LipSync = GameObject.Find("Frank");
        lipSync = LipSync.GetComponent<LipSync>();

        CurrentState = AgentState.Idle;
        audioSource2 = GetComponent<AudioSource>();

        ChatObject = GameObject.Find("Chat UIDocument");
        chat = ChatObject.GetComponent<Chat>();
        responses = new Queue<string>();

        anim             = GameObject.Find("Frank").GetComponent<AnimController>();
        emotionController = GameObject.Find("Frank").GetComponent<EmotionController>();
        headLookAt       = GameObject.Find("Frank").GetComponent<HeadLookAt>();
        recIndicator     = GameObject.Find("Sphere").GetComponent<RecIndicator>();

        _agentInitialPosition = agent.transform.position;

        if (serverConnector == null)
            Debug.LogError("[Controller] serverConnector is not assigned in the Inspector.");
        else
        {
            serverConnector.OnConnected       += OnServerConnected;
            serverConnector.OnUserTranscript  += OnUserTranscript;
            serverConnector.OnTextReply       += OnAgentTextReply;
            serverConnector.OnTtsComplete     += OnAgentTtsReady;
            serverConnector.OnMovementCommand  += OnAgentMovement;
            serverConnector.OnAnimationCommand += OnAgentAnimation;
            serverConnector.OnAvatarCommand    += OnAgentAvatarChange;
            serverConnector.OnEmotionCommand   += OnAgentEmotion;
            serverConnector.OnLooksCommand     += OnAgentLooks;
        }
    }

    private void OnDestroy()
    {
        if (serverConnector != null)
        {
            serverConnector.OnConnected        -= OnServerConnected;
            serverConnector.OnUserTranscript   -= OnUserTranscript;
            serverConnector.OnTextReply        -= OnAgentTextReply;
            serverConnector.OnTtsComplete      -= OnAgentTtsReady;
            serverConnector.OnMovementCommand  -= OnAgentMovement;
            serverConnector.OnAnimationCommand -= OnAgentAnimation;
            serverConnector.OnAvatarCommand    -= OnAgentAvatarChange;
            serverConnector.OnEmotionCommand   -= OnAgentEmotion;
            serverConnector.OnLooksCommand     -= OnAgentLooks;
        }
    }

    // ── OvarpServerConnector event handlers ───────────────────────────────────

    private void OnServerConnected() => _inputEnabled = true;

    private void OnUserTranscript(string text)
    {
        chat.killTempUser();
        chat.sendUserMessage(text);
    }

    private void OnAgentTextReply(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        anim.StopThinking();
        ReceiveMessage(text);
    }

    private void OnAgentTtsReady(AudioClip clip)
    {
        audioSource.clip = clip;
        lipSync.audioSource.clip = clip;
        isAudioReady = true;
    }

    private void OnAgentMovement(string direction)
    {
        if (direction == "reset_position")
        {
            agent.transform.position = _agentInitialPosition;
            return;
        }

        // Movement relative to the user (camera) horizontal orientation
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        Vector3 forward = cam != null
            ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized
            : Vector3.forward;
        Vector3 right = cam != null
            ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized
            : Vector3.right;

        Vector3 delta = direction switch
        {
            "move_closer"  => -forward,
            "move_farther" => forward,
            "move_left"    => -right,
            "move_right"   => right,
            _              => Vector3.zero
        };

        agent.transform.position += delta * MoveStep;
    }

    private void OnAgentAnimation(string animName)
    {
        anim.PlayAnimation(animName);
    }

    private void OnAgentEmotion(string emotion)
    {
        emotionController?.SetEmotion(emotion);
    }

    private void OnAgentLooks(string lookTarget)
    {
        headLookAt?.SetLookTarget(lookTarget);
    }

    private void OnAgentAvatarChange(string avatarName)
    {
        // TODO: implement avatar prefab swap
        Debug.Log($"[Controller] Avatar change requested: '{avatarName}' — not yet implemented.");
    }

    // ── Update / state machine ────────────────────────────────────────────────

    void Update()
    {
        FixAudioClicks();

        if (CurrentState == AgentState.Idle)
        {
            if (!paused && utteranceQueue.Count > 0)
            {
                CurrentState = AgentState.Waiting;
                CurrentUtterance = utteranceQueue.Dequeue();
                CurrentIntent = intentQueue.Dequeue();
            }
        }
        else if (CurrentState == AgentState.Waiting)
        {
            if (isAudioReady)
            {
                CurrentState = AgentState.Speaking;
                _playbackStarted = false;
                StartCoroutine(lipSync.AnalyzeAudioClip(audioSource.clip));
            }
        }
        else if (CurrentState == AgentState.Speaking)
        {
            if (audioSource.isPlaying) _playbackStarted = true;
            if (_playbackStarted && !audioSource.isPlaying)
            {
                CurrentState = AgentState.Idle;
                isAudioReady = false;
                _playbackStarted = false;
                CurrentIntent = null;
            }
        }

        if (responses.Count != 0)
        {
            chat.killTempAgent();
            chat.sendAgentMessage(responses.Dequeue());
        }

        CheckInputTriggers();
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    private bool _inputEnabled = false;
    private bool rightTriggerWasPressed = false;
    private bool _touchWasActive = false;

    private void CheckInputTriggers()
    {
        if (!_inputEnabled) return;

        // Desktop: spacebar toggle
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            ProcessInput();
            return;
        }

        // XREAL Beam Pro: screen tap toggle — gate on rising edge to avoid multi-frame re-trigger
        if (Touchscreen.current != null)
        {
            var touch = Touchscreen.current.primaryTouch;
            var phase = touch.phase.ReadValue();
            bool touchActive = phase != UnityEngine.InputSystem.TouchPhase.None
                            && phase != UnityEngine.InputSystem.TouchPhase.Ended
                            && phase != UnityEngine.InputSystem.TouchPhase.Canceled;
            if (touchActive && !_touchWasActive)
            {
                // Skip mic trigger when the tap lands on a UI element (e.g. ServerSetupUI buttons)
                int fingerId = (int)touch.touchId.ReadValue();
                bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(fingerId);
                if (!overUI)
                    ProcessInput();
                _touchWasActive = true;
                return;
            }
            if (!touchActive)
                _touchWasActive = false;
        }

        // Meta Quest: right controller trigger (press to start, release to stop)
        var rightHandDevices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller,
            rightHandDevices
        );

        if (rightHandDevices.Count > 0)
        {
            rightHandDevices[0].TryGetFeatureValue(XRCommonUsages.triggerButton, out bool triggerPressed);
            if (triggerPressed && !rightTriggerWasPressed)
                ProcessInput();
            else if (!triggerPressed && rightTriggerWasPressed)
                ProcessInput();
            rightTriggerWasPressed = triggerPressed;
        }
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    public void ProcessInput()
    {
        if (!isListening)
        {
            recIndicator.StartBlinking();
            StartRecording();
            chat.SendTempUser();
            isListening = true;
        }
        else
        {
            recIndicator.StopBlinking();
            bool sent = StopRecording();
            if (sent) anim.StartThinking();
            isListening = false;
        }
    }

    IEnumerator RequestMicrophonePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            Permission.RequestUserPermission(Permission.Microphone);

        // Wait up to 10s for the user to respond to the permission dialog
        float timeout = 10f;
        while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && timeout > 0)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }
        hasMicPermission = Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        hasMicPermission = Application.HasUserAuthorization(UserAuthorization.Microphone);
#endif
        if (!hasMicPermission)
            Debug.LogError("Microphone permission denied — voice input will not work.");
    }

    public void StartRecording()
    {
        if (!hasMicPermission)
        {
            Debug.LogWarning("StartRecording: microphone permission not granted.");
            return;
        }
        outputFilePath = Application.persistentDataPath + "/mic.wav";
        // Loop=true, maxLength=10s — enough for a conversational turn
        audioSource2.clip = Microphone.Start(null, true, 10, AudioSettings.outputSampleRate);
    }

    // Returns true if audio was captured and sent, false if nothing was recorded.
    public bool StopRecording()
    {
        // Capture actual recorded length before stopping to avoid trailing silence
        int recordedSamples = Microphone.GetPosition(null);
        Microphone.End(null);

        if (recordedSamples <= 0 || audioSource2.clip == null)
        {
            Debug.LogWarning("StopRecording: no audio captured.");
            return false;
        }

        // Trim clip to recorded length
        int channels = audioSource2.clip.channels;
        float[] data = new float[recordedSamples * channels];
        audioSource2.clip.GetData(data, 0);
        AudioClip trimmed = AudioClip.Create("mic", recordedSamples, channels, audioSource2.clip.frequency, false);
        trimmed.SetData(data, 0);

        SavWav.Save("mic.wav", trimmed);
        serverConnector.SendAudio(File.ReadAllBytes(outputFilePath));
        return true;
    }

    // ── Speech queue ──────────────────────────────────────────────────────────

    public void ReceiveMessage(string text, bool immediate = false)
    {
        utteranceQueue.Enqueue(text);
        intentQueue.Enqueue(null);
        responses.Enqueue(text);
        Hello(text);
    }

    public void SpeakIntent(string message, bool immediate = false)
    {
        Speak(message, immediate);
    }

    public void Speak(string utterance, bool immediate = false, string[] intent = null)
    {
        if (immediate)
        {
            utteranceQueue.Clear();
            intentQueue.Clear();
            CurrentState = AgentState.Idle;
        }
        utteranceQueue.Enqueue(utterance);
        intentQueue.Enqueue(intent);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FixAudioClicks()
    {
        // Fade in/out to hide TTS audio clicks at start/end
        if (audioSource.clip == null) return;

        if (audioSource.time < audioFadeIn)
        {
            audioSource.volume = audioSource.time / audioFadeIn;
        }
        else if (audioSource.time > audioSource.clip.length - audioFadeOut)
        {
            audioSource.volume = (audioSource.clip.length - audioSource.time) / audioFadeOut;
        }
        else
        {
            audioSource.volume = 1;
        }
    }

    public void SetName(string myString)
    {
        string[] elements = myString.Split('|');
        Print(elements[1]);
    }

    public void Quit()
    {
        QuitGame();
    }
}
