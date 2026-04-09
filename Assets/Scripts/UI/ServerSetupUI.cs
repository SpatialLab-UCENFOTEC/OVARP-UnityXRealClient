// Uses UI Toolkit (same pipeline as Chat) — requires a UIDocument component on this GameObject.
// Assign the same PanelSettings used by the Chat UIDocument in the Inspector.
using UnityEngine;
using UnityEngine.UIElements;

public class ServerSetupUI : MonoBehaviour
{
    [SerializeField] private OvarpServerConnector serverConnector;
    [SerializeField] private UIDocument chatDocument;
    [SerializeField] private string defaultIp = "192.168.100.38";

    private const string PlayerPrefsKey = "ovarp_server_ip";
    private const string LegacyPlayerPrefsKey = "ovaf_server_ip";

    private VisualElement _root;
    private TextField _ipField;
    private void Start()
    {
        _root = GetComponent<UIDocument>().rootVisualElement;
        BuildUI();

        // Hide chat visually and block its input without disabling the UIDocument —
        // disabling it in Awake breaks Chat.OnEnable() which initializes from rootVisualElement.
        if (chatDocument != null)
        {
            chatDocument.rootVisualElement.style.display = DisplayStyle.None;
            chatDocument.rootVisualElement.pickingMode = PickingMode.Ignore;
        }

        if (serverConnector != null)
            serverConnector.OnConnected += OnConnected;
        else
            Debug.LogError("[ServerSetupUI] serverConnector is not assigned in the Inspector.");
    }

    private void OnDestroy()
    {
        if (serverConnector != null)
            serverConnector.OnConnected -= OnConnected;
    }

    private void OnConnected()
    {
        if (chatDocument != null)
        {
            chatDocument.rootVisualElement.style.display = DisplayStyle.Flex;
            chatDocument.rootVisualElement.pickingMode = PickingMode.Position;
        }

        _root.Clear();
        _root.style.display = DisplayStyle.None;
        enabled = false;
    }

    private void BuildUI()
    {
        _root.Clear();
        _root.pickingMode = PickingMode.Ignore;

        // Compact card anchored to top-left
        var card = new VisualElement();
        card.style.position = Position.Absolute;
        card.style.top = 16;
        card.style.left = 16;
        card.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 0.95f);
        card.style.paddingTop = card.style.paddingBottom = 16;
        card.style.paddingLeft = card.style.paddingRight = 16;
        card.pickingMode = PickingMode.Position;
        _root.Add(card);

        var title = new Label("OVARP server IP");
        title.style.color = Color.white;
        title.style.fontSize = 20;
        title.style.unityTextAlign = TextAnchor.MiddleLeft;
        title.style.marginBottom = 10;
        card.Add(title);

        _ipField = new TextField();
        string saved = PlayerPrefs.GetString(PlayerPrefsKey, "");
        if (string.IsNullOrEmpty(saved) && PlayerPrefs.HasKey(LegacyPlayerPrefsKey))
        {
            saved = PlayerPrefs.GetString(LegacyPlayerPrefsKey, defaultIp);
            PlayerPrefs.SetString(PlayerPrefsKey, saved);
            PlayerPrefs.Save();
        }
        _ipField.value = string.IsNullOrEmpty(saved) ? defaultIp : saved;
        _ipField.style.fontSize = 18;
        _ipField.style.marginBottom = 10;
        _ipField.style.minWidth = 240;
        card.Add(_ipField);

        var btn = new Button { text = "Connect" };
        btn.style.fontSize = 18;
        btn.style.paddingLeft = btn.style.paddingRight = 16;
        btn.style.paddingTop = btn.style.paddingBottom = 8;
        btn.style.backgroundColor = new Color(0f, 0.47f, 1f, 1f);
        btn.style.color = Color.white;
        // PointerDownEvent for editor; ClickEvent for XREAL (XR UI Input Module dispatches Click for touch)
        btn.RegisterCallback<PointerDownEvent>(_ => OnConnectClicked());
        btn.RegisterCallback<ClickEvent>(_ => OnConnectClicked());
        card.Add(btn);
    }

    private void OnConnectClicked()
    {
        string ip = _ipField.value.Trim();
        if (string.IsNullOrEmpty(ip)) ip = defaultIp;

        PlayerPrefs.SetString(PlayerPrefsKey, ip);
        PlayerPrefs.Save();

        if (serverConnector == null) { Debug.LogError("[ServerSetupUI] serverConnector not assigned."); return; }
        serverConnector.ConnectToServer($"ws://{ip}:8000");
    }
}
