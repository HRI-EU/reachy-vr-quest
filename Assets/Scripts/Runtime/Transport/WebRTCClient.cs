using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TMPro;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;

[Serializable] public class SdpMsgUI { public string type; public string sdp; }
[Serializable] public class IceMsgUI { public string type; public string candidate; public string sdpMid; public int sdpMLineIndex; }
[Serializable] public class MsgBaseUI { public string type; }

public class WebRTCClient : MonoBehaviour
{
    [Header("UI")]
    public RawImage receiveRawImage;     // RawImage on the Canvas, used to display remote video.
    public GameObject holoOverlay;       // Optional existing overlay.

    [Header("Signaling (LAN)")]
    public string signalingUrl = "ws://127.0.0.1:8766"; // Use the PC LAN IP on Quest.

    [Tooltip("Input field for user-provided address (e.g., 10.0.71.38:8766)")]
    [SerializeField] private TMP_InputField userInputAddress;

    private RTCPeerConnection pc;
    private WebSocket ws;
    private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
    private string pendingOfferSdp;
    private bool isConnecting;

    // Keep WebRTC.Update() running.
    private Coroutine webrtcUpdateCoroutine;
    private Coroutine connectCoroutine;

    // WebRTC 3.0: OnVideoReceived is usually only used for the initial binding.
    private bool boundVideoTexture = false;

    // Debug counters
    private int onVideoReceivedCount = 0;
    private float lastOnVideoLogTime = 0f;

    // Instance key for PlayerPrefs
    private string InstanceKey => $"WebRTCClient_{gameObject.name}";

    public bool IsConnectedOrConnecting => isConnecting || (ws != null && ws.IsAlive);
    public event Action<bool> ConnectionStateChanged;

    private void Start()
    {
        if (receiveRawImage != null)
        {
            receiveRawImage.enabled = false;
            receiveRawImage.texture = null;
            receiveRawImage.color = Color.white;
        }
        if (holoOverlay != null) holoOverlay.SetActive(false);

#if !UNITY_EDITOR
        // Load saved address if available
        string urlKey = $"{InstanceKey}_SignalingUrl";
        if (PlayerPrefs.HasKey(urlKey))
            signalingUrl = PlayerPrefs.GetString(urlKey);
#endif

        SetupPeerConnection();

        // WebRTC.Update() should keep running in WebRTC 3.0.
        if (webrtcUpdateCoroutine == null)
            webrtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
    }

    /// <summary>
    /// Parses the user-provided address in IP:PORT format, for example 10.0.71.38:8766.
    /// </summary>
    private bool TryParseAddress(string input, out string ip, out int port)
    {
        ip = null;
        port = 0;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var parts = input.Trim().Split(':');
        if (parts.Length != 2)
            return false;

        // Validate IP address
        if (!IPAddress.TryParse(parts[0], out _))
            return false;

        // Validate port
        if (!int.TryParse(parts[1], out port) || port < 1 || port > 65535)
            return false;

        ip = parts[0];
        return true;
    }

    private void SetupPeerConnection()
    {
        var cfg = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() };
        pc = new RTCPeerConnection(ref cfg);

        pc.OnConnectionStateChange = s => Debug.Log($"[RecvUI] ConnState: {s}");

        pc.OnIceCandidate = cand =>
        {
            if (cand == null || ws == null || !ws.IsAlive) return;
            var msg = new IceMsgUI
            {
                type = "ice",
                candidate = cand.Candidate,
                sdpMid = cand.SdpMid,
                sdpMLineIndex = cand.SdpMLineIndex ?? 0
            };
            ws.Send(JsonUtility.ToJson(msg));
        };

        pc.OnTrack = e =>
        {
            Debug.Log($"[RecvUI] OnTrack kind={e.Track.Kind}");

            if (e.Track is VideoStreamTrack v)
            {
                // WebRTC 3.0: this callback often fires only once, on the first frame.
                v.OnVideoReceived += tex =>
                {
                    if (tex == null) return;

                    // Count callback frequency to help determine whether it only fires once.
                    onVideoReceivedCount++;
                    if (Time.time - lastOnVideoLogTime > 1f)
                    {
                        Debug.Log($"[RecvUI] OnVideoReceived callback rate ~ {onVideoReceivedCount}/sec");
                        onVideoReceivedCount = 0;
                        lastOnVideoLogTime = Time.time;
                    }

                    mainThread.Enqueue(() =>
                    {
                        // Bind the texture only once, matching the common WebRTC 3.0 behavior.
                        if (!boundVideoTexture)
                        {
                            boundVideoTexture = true;

                            if (receiveRawImage != null)
                            {
                                receiveRawImage.texture = tex;
                                receiveRawImage.enabled = true;

                                // Optional: preserve the aspect ratio if the RawImage has an AspectRatioFitter component.
                                // var fitter = receiveRawImage.GetComponent<AspectRatioFitter>();
                                // if (fitter != null) fitter.aspectRatio = (float)tex.width / tex.height;

                                Debug.Log($"[RecvUI] Bound texture once: {tex.width}x{tex.height} {tex.GetType()}");
                            }

                            if (holoOverlay != null) holoOverlay.SetActive(true);
                        }
                    });
                };
            }
        };

        // RecvOnly video transceiver
        pc.AddTransceiver(TrackKind.Video,
            new RTCRtpTransceiverInit { direction = RTCRtpTransceiverDirection.RecvOnly });
    }

    public bool Connect(string newSignalingUrl)
    {
        if (!IsValidSignalingUrl(newSignalingUrl, out string normalizedUrl))
        {
            Debug.LogError($"[RecvUI] Invalid signaling URL: {newSignalingUrl}");
            return false;
        }

        signalingUrl = normalizedUrl;
        Connect();
        return true;
    }

    public void Connect()
    {
        Debug.Log($"[RecvUI] Connect() requested url={signalingUrl}");

        if (IsConnectedOrConnecting)
        {
            Debug.Log("[RecvUI] WebRTC signaling already connected or connecting");
            return;
        }

        if (pc == null)
            SetupPeerConnection();

        // Allow the texture to be rebound once for each new connection.
        boundVideoTexture = false;
        ShowVideoSurface(true);
        isConnecting = true;
        ConnectionStateChanged?.Invoke(true);

        connectCoroutine = StartCoroutine(CreateOfferAndConnect());
    }

    public void Disconnect()
    {
        Debug.Log("[RecvUI] Disconnect() requested");

        if (connectCoroutine != null)
        {
            StopCoroutine(connectCoroutine);
            connectCoroutine = null;
        }

        isConnecting = false;
        ConnectionStateChanged?.Invoke(false);

        // Close signaling
        try
        {
            if (ws != null && ws.IsAlive) ws.CloseAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError("[RecvUI] WS CloseAsync error: " + ex);
        }
        finally
        {
            ws = null;
        }

        // Close WebRTC
        try
        {
            if (pc != null)
            {
                pc.Close();
                pc.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[RecvUI] PC dispose error: " + ex);
        }
        finally
        {
            pc = null;
        }

        pendingOfferSdp = null;

        mainThread.Enqueue(() =>
        {
            boundVideoTexture = false;
            if (receiveRawImage != null)
            {
                receiveRawImage.texture = null;
                receiveRawImage.enabled = false;
            }
            if (holoOverlay != null) holoOverlay.SetActive(false);
        });

        Debug.Log($"[RecvUI] WS state: {(ws != null ? (ws.IsAlive ? "Alive" : "Dead") : "null")}");
    }

    public static bool IsValidSignalingUrl(string url, out string normalizedUrl)
    {
        normalizedUrl = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        string trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.Port > 65535)
            return false;

        normalizedUrl = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }

    /// <summary>
    /// Called when the user clicks the button to toggle connection or disconnection.
    /// </summary>
    public void ToggleConnect()
    {
        Debug.Log("[RecvUI] ToggleConnect() requested");

        if (ws != null && ws.IsAlive)
        {
            Disconnect();
        }
        else
        {
            UserConnectWithAddress();
        }
    }

    /// <summary>
    /// Parses the user-provided address and connects in IP:PORT format.
    /// </summary>
    private void UserConnectWithAddress()
    {
#if !UNITY_EDITOR
        if (userInputAddress == null)
        {
            Debug.LogError("[RecvUI] userInputAddress field is not assigned in Inspector.");
            return;
        }

        var rawInput = userInputAddress.text.Trim();

        // Parse and validate address
        if (!TryParseAddress(rawInput, out var ip, out var port))
        {
            Debug.LogError("[RecvUI] Invalid address format. Use format like 10.0.71.38:8766");
            return;
        }

        // Build signaling URL
        signalingUrl = $"ws://{ip}:{port}";

        // Save for persistence
        PlayerPrefs.SetString($"{InstanceKey}_SignalingUrl", signalingUrl);
        PlayerPrefs.Save();

        Debug.Log($"[RecvUI] New signaling URL saved: {signalingUrl}");
#endif

        Connect();
    }

    private System.Collections.IEnumerator CreateOfferAndConnect()
    {
        Debug.Log("[RecvUI] CreateOffer...");

        var offerOp = pc.CreateOffer();
        yield return offerOp;

        var offer = offerOp.Desc;
        var setLocalOp = pc.SetLocalDescription(ref offer);
        yield return setLocalOp;

        pendingOfferSdp = offer.sdp;
        ConnectWS();
        connectCoroutine = null;
    }

    private void ConnectWS()
    {
        Debug.Log($"[RecvUI][WS] Connecting to {signalingUrl}");
        ws = new WebSocket(signalingUrl);
        ws.EmitOnPing = true;

        ws.OnOpen += (s, e) =>
        {
            Debug.Log("[RecvUI][WS] Open");
            isConnecting = false;
            ws.Send(JsonUtility.ToJson(new SdpMsgUI { type = "offer", sdp = pendingOfferSdp }));
        };

        ws.OnMessage += (s, e) =>
        {
            var json = e.Data ?? Encoding.UTF8.GetString(e.RawData);
            MsgBaseUI head = null;
            try { head = JsonUtility.FromJson<MsgBaseUI>(json); } catch { }

            if (head == null || string.IsNullOrEmpty(head.type)) return;

            if (head.type == "answer")
            {
                var sdp = JsonUtility.FromJson<SdpMsgUI>(json);
                mainThread.Enqueue(() =>
                {
                    var ans = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp.sdp };
                    StartCoroutine(SetRemoteCoroutine(ans));
                });
            }
            else if (head.type == "ice")
            {
                var ice = JsonUtility.FromJson<IceMsgUI>(json);
                mainThread.Enqueue(() =>
                {
                    var c = new RTCIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = ice.candidate,
                        sdpMid = ice.sdpMid,
                        sdpMLineIndex = ice.sdpMLineIndex
                    });
                    if (pc != null)
                        pc.AddIceCandidate(c);
                });
            }
        };

        ws.OnError += (s, e) =>
        {
            isConnecting = false;
            mainThread.Enqueue(HandleSignalingStopped);
            Debug.LogError("[RecvUI][WS] Error: " + e.Message);
        };
        ws.OnClose += (s, e) =>
        {
            isConnecting = false;
            mainThread.Enqueue(HandleSignalingStopped);
            Debug.Log("[RecvUI][WS] Closed: " + e.Reason);
        };

        ws.ConnectAsync();
    }

    private void HandleSignalingStopped()
    {
        isConnecting = false;
        pendingOfferSdp = null;
        ws = null;

        try
        {
            if (pc != null)
            {
                pc.Close();
                pc.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[RecvUI] PC cleanup after signaling stop failed: " + ex);
        }
        finally
        {
            pc = null;
        }

        boundVideoTexture = false;
        if (receiveRawImage != null)
        {
            receiveRawImage.texture = null;
            ShowVideoSurface(false);
        }

        if (holoOverlay != null)
            holoOverlay.SetActive(false);

        ConnectionStateChanged?.Invoke(false);
    }

    private void ShowVideoSurface(bool visible)
    {
        if (receiveRawImage == null)
            return;

        receiveRawImage.enabled = visible;
        receiveRawImage.color = Color.white;
    }

    private System.Collections.IEnumerator SetRemoteCoroutine(RTCSessionDescription desc)
    {
        var d = desc;
        var op = pc.SetRemoteDescription(ref d);
        yield return op;
        Debug.Log("[RecvUI] SetRemoteDescription done");
    }

    private void Update()
    {
        while (mainThread.TryDequeue(out var a))
        {
            try { a?.Invoke(); }
            catch (Exception ex) { Debug.LogError(ex); }
        }
    }

    private void OnDestroy()
    {
        if (connectCoroutine != null)
        {
            StopCoroutine(connectCoroutine);
            connectCoroutine = null;
        }

        try { if (ws != null && ws.IsAlive) ws.CloseAsync(); } catch { }

        pc?.Close();
        pc?.Dispose();
        pc = null;
        ws = null;
        isConnecting = false;
        ConnectionStateChanged?.Invoke(false);

        if (webrtcUpdateCoroutine != null)
        {
            StopCoroutine(webrtcUpdateCoroutine);
            webrtcUpdateCoroutine = null;
        }
    }
}
