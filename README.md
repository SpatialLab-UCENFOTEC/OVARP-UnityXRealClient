<p align="center">
  <img src="https://img.shields.io/badge/unity-6%20(URP)-black?logo=unity&logoColor=white" alt="Unity" />
  <img src="https://img.shields.io/badge/platform-XREAL%20%7C%20Desktop-blue" alt="Platform" />
  <img src="https://img.shields.io/badge/XREAL%20XR%20Plugin-local%20package-00A8E8" alt="XREAL XR" />
  <img src="https://img.shields.io/badge/csharp-11-purple?logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License" />
</p>

<h1 align="center">🥽 Open Virtual Agent Research Platform — Unity XREAL Client</h1>

<p align="center">
  <strong>XR-native Unity client (XREAL glasses &amp; desktop) for driving embodied conversational agents across immersive platforms, featuring real-time voice interaction, lip-sync, emotion, gestures, and spatial movement — with a transparent OpenAI fallback when the OVARP server is unreachable.</strong>
</p>

<p align="center">
  <em>Reference Unity implementation of the OVARP client protocol for XREAL devices (XREAL XR Plugin instead of OpenXR for Quest).</em>
</p>

---

## ✨ What is this repo?

**This repository** is an **XREAL**-targeted Unity frontend for **OVARP** (Open Virtual Agent Research Platform). It shares the same runtime architecture as the Meta Quest / OpenXR variant: it connects to an OVARP-compatible backend and renders a fully animated 3D avatar — synchronized speech, lip-sync, facial emotions, gestures, gaze, and spatial movement — using the **XREAL XR Plugin** (`com.xreal.xr`) instead of OpenXR for Quest.

- 🎙️ **Voice capture** — Microphone recording with WAV trimming, streamed to the server as base64 audio
- 👄 **Lip-sync** — Real-time amplitude analysis drives blendshape-based mouth animation
- 😊 **Emotion system** — Server-commanded facial expressions via blendshapes
- 🤖 **Gesture playback** — Named animation triggers (`clap`, `bow`, `thumbs_up`, `dance`, `thinking`, etc.)
- 👀 **Head look-at** — Agent gaze directed at the user, away, or another agent
- 📐 **Spatial movement** — Agent repositioned relative to user camera orientation (`move_closer`, `move_farther`, `move_left`, `move_right`, `reset_position`)
- 💬 **Chat UI** — Scrollable conversation log with user/agent bubbles, theming, and dark mode
- 🖥️ **Server setup UI** — XR-friendly IP entry panel before session starts; IP persisted via PlayerPrefs
- 🔁 **OpenAI fallback** — Transparent fallback to Whisper → GPT-4 → TTS-1 when the OVARP server is unreachable

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  User                                                           │
│  ────                                                           │
│  • Speaks into microphone                                       │
│  • Input: Spacebar (desktop) | Screen tap (XREAL Beam Pro) |    │
│           Right trigger (XR controller, when available)         │
└────────────────────┬────────────────────────────────────────────┘
                     │ WAV audio (base64)
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│  Controller.cs — Agent State Machine                            │
│  ───────────────────────────────────                            │
│  ┌───────────┐  ┌──────────────┐  ┌──────────────┐             │
│  │   Idle    │─▶│   Waiting    │─▶│   Speaking   │             │
│  │           │  │              │  │              │             │
│  │ • listens │  │ • awaits TTS │  │ • plays clip │             │
│  │ • records │  │ • buffers    │  │ • lip-syncs  │             │
│  └───────────┘  └──────────────┘  └──────────────┘             │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│  OvarpServerConnector.cs — WebSocket + OpenAI fallback          │
│  ─────────────────────────────────────                          │
│  ┌──────────────────────┐  ┌───────────────────────────────┐    │
│  │  OVARP Server (WS)   │  │  OpenAI Fallback              │    │
│  │                      │  │                               │    │
│  │  ws://<host>:8000    │  │  Whisper → GPT-4 → TTS-1      │    │
│  │  /ws/client/<id>     │  │  (activated on connect fail)  │    │
│  └──────────────────────┘  └───────────────────────────────┘    │
│                                                                 │
│  Events fired back to Controller (main thread):                 │
│  OnTextReply · OnTtsComplete · OnUserTranscript                 │
│  OnMovementCommand · OnAnimationCommand · OnEmotionCommand      │
│  OnLooksCommand · OnAvatarCommand                               │
└─────────────────────────────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│  Avatar Components                                              │
│  ─────────────────                                              │
│  • AnimController    — named animation triggers                 │
│  • EmotionController — blendshape facial expressions            │
│  • LipSync           — amplitude → mouth blendshape             │
│  • HeadLookAt        — gaze target switching                    │
│  • GesturePlayer     — gesture clip sequencing                  │
│  • BlinkEyes         — procedural eye blink                     │
└─────────────────────────────────────────────────────────────────┘
```

`Controller` is backend-agnostic — it reacts only to events from `OvarpServerConnector`, regardless of whether they originate from the OVARP server or the OpenAI fallback.

---

## 🚀 Quick Start

### Prerequisites

- Unity 6 (URP)
- **XREAL XR Plugin** — this project references `com.xreal.xr` as a **local tarball** (`file:../com.xreal.xr.tar.gz`). Place the vendor `com.xreal.xr.tar.gz` next to the repository root (sibling of the cloned folder) **or** update the path in `Packages/manifest.json` to match your machine.
- XREAL device (for XR deployment) or desktop for editor testing
- A running **OVARP-compatible WebSocket server** on the same LAN, **or** an [OpenAI API key](https://platform.openai.com/api-keys) for the fallback path

### Installation

```bash
git clone <this-repository-url>
cd <repository-folder>
```

Open the project in Unity 6 with the Universal Render Pipeline. Ensure the **XREAL** package resolves (see above).

### Configure `OvarpServerConnector`

Select the GameObject with the **Ovarp Server Connector** component in the Inspector and set:

| Field | Description |
|---|---|
| `Editor Server URL` | WebSocket URL for local development (e.g. `ws://localhost:8000`) |
| `Quest Server URL` | WebSocket URL for the LAN server when running on **Android** device builds (XREAL uses this field on device) |
| `Client Id` | Unique identifier for this client connection (default in `XrealOVAF.unity`: `xreal_01`) |
| `Sender` | Origin device id in each message body (should match your server / routing expectations; scene default: `xreal_01`) |
| `Target Agent` | Agent ID to address on the server (must match `config.yaml`) |
| `OpenAI Api Key` | Required only for the OpenAI fallback path |

At runtime, the URL can be overridden through the **Server Setup UI** panel before the session starts.

### Build for XREAL (Android)

Follow the current **XREAL developer documentation** for Unity (XR Plugin Management, Android player settings, and deployment). Typical steps:

```bash
# 1. Switch platform to Android (File > Build Settings)
# 2. Configure XR Plug-in Management for the XREAL loader (com.xreal.xr)
# 3. Set package name, minimum API level, and signing per XREAL / Play requirements
# 4. Build and deploy to the device (ADB or vendor tools)
```

### Desktop / Editor

Press **Spacebar** to toggle recording. The client auto-selects `editorServerUrl` when not running on Android.

---

## 🎮 Avatar System

The avatar is driven entirely by server-sent events. Each event type maps to a dedicated component:

### 🎙️ Lip-Sync
Real-time per-frame amplitude analysis of the TTS `AudioClip` drives mouth blendshapes in `LipSync.cs`. No phoneme data required — works with any TTS provider output.

### 😊 Emotion Control
`EmotionController.cs` receives string commands from the server (`neutral`, `happy`, `sad`, `angry`, `surprised`) and blends the corresponding facial blendshapes.

### 🤖 Gestures & Animations
`AnimController.cs` maps named string commands to Animator triggers. `GesturePlayer.cs` handles clip sequencing for multi-step gestures. Supported values are defined in the OVARP server's `config.yaml`.

### 👀 Gaze
`HeadLookAt.cs` accepts look targets from the server — `user` (main camera), `away`, or another agent ID — and smoothly transitions the avatar's head orientation.

### 📐 Spatial Movement
`Controller.cs` handles movement commands relative to the current camera orientation, so `move_closer` / `move_farther` always move toward/away from the user regardless of their position.

---

## ⚙️ Configuration

### WebSocket Protocol

The client sends audio to the server as:

```json
{
  "sender": "xreal_01",
  "target_device": "all",
  "target_agent": "agent_alpha",
  "command_type": "audio",
  "command": "stt_request",
  "subcommand": { "audio_base64": "<base64-encoded WAV>" }
}
```

The server responds across the following topics:

| Topic | Command | Payload |
|---|---|---|
| `message` | `llm_reply` | `text` |
| `message` | `user_transcript` | `text` |
| `audio` | `tts_chunk` | `audio_base64` — streamed PCM WAV chunks |
| `audio` | `tts_complete` | signals end of TTS stream |
| `action` | `execute_state` | `movement` / `actions` / `avatar` / `emotions` / `looks` |

### GameManager

`GameManager.cs` is a singleton that holds session-scoped state. Configure via Inspector or at runtime:

| Setting | Default | Description |
|---|---|---|
| Agent name | `Nova` | Displayed in the chat UI |
| User name | `User` | Displayed in the chat UI |
| User bubble color | Blue | Chat bubble tint |
| Agent bubble color | Blue | Chat bubble tint |
| Dark mode | Off | UI theme toggle |

---

## 📁 Project Structure

```
<repo>/
│
├── Assets/
│   ├── Scenes/
│   │   └── XrealOVAF.unity          # Main OVARP session scene
│   │
│   ├── Scripts/
│   │   ├── Controller.cs            # Agent state machine, input, audio recording/playback
│   │   ├── OvarpServerConnector.cs  # OVARP WebSocket client + OpenAI fallback pipeline
│   │   ├── GameManager.cs           # Singleton session config (names, colors, dark mode)
│   │   ├── AnimController.cs        # Named animation triggers and thinking state
│   │   ├── EmotionController.cs     # Blendshape-based facial expression control
│   │   ├── HeadLookAt.cs            # Gaze target switching
│   │   ├── GesturePlayer.cs         # Gesture clip sequencing
│   │   ├── LipSync.cs               # Amplitude → mouth blendshape mapping
│   │   ├── BlinkEyes.cs             # Procedural eye blink animation
│   │   ├── RecIndicator.cs          # Recording state blink indicator
│   │   ├── SavWav.cs                # AudioClip → PCM WAV serialization
│   │   └── UI/
│   │       ├── Chat.cs              # UI Toolkit chat log
│   │       ├── ScrollSpeed.cs       # Chat scroll behavior
│   │       └── ServerSetupUI.cs     # Pre-session panel for server IP entry
│   │
│   ├── Avatars/                     # Avatar prefabs and assets
│   ├── XR/                          # XR management / loader settings
│   └── Samples/                     # URP, XR Hands, XRIT samples (optional)
│
├── Packages/
│   └── manifest.json                # Unity package dependencies (incl. com.xreal.xr)
│
└── ProjectSettings/
    └── ProjectSettings.asset        # Build targets, XR configuration
```

---

## 🎯 Roadmap

- [x] ~~WebSocket connection to OVARP server~~
- [x] ~~OpenAI fallback pipeline (Whisper → GPT-4 → TTS-1)~~
- [x] ~~Lip-sync from TTS audio~~
- [x] ~~Emotion and gesture command handling~~
- [x] ~~Spatial movement relative to camera~~
- [x] ~~XR-native server setup UI~~
- [x] ~~XREAL Beam Pro screen tap + optional XR controller trigger input~~
- [ ] Avatar prefab hot-swap at runtime
- [ ] Voice activity detection (VAD) for natural turn-taking
- [ ] ZeroMQ transport support (lower latency on-device)
- [ ] Multi-agent scene support

---

## Contributing

Issues and pull requests are welcome.

---

## License

MIT License © 2026 [Anonymous]

See [LICENSE](LICENSE) for the full license text.
