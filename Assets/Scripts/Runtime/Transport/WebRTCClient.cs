using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;

[Serializable] public class SdpMsgUI { public string type; public string sdp; }
[Serializable] public class IceMsgUI { public string type; public string candidate; public string sdpMid; public int sdpMLineIndex; }

public class WebRTCClient : MonoBehaviour
{
    [Header("UI")]
    public RawImage receiveRawImage;
    public GameObject holoOverlay;

    [Header("GStreamer Signaling (LAN)")]
    public string signalingUrl = "ws://127.0.0.1:8443";
    public string listenerName = "unity-teleop";
    public string preferredProducerName = "reachymini";

    [Tooltip("Legacy input field. The in-scene Robot IP field is managed by ReachyVideoInputController.")]
    [SerializeField] private TMP_InputField userInputAddress;

    private RTCPeerConnection pc;
    private RTCDataChannel dataChannel;
    private WebSocket ws;
    private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
    private readonly List<IceMsgUI> pendingRemoteIce = new List<IceMsgUI>();
    private Coroutine webrtcUpdateCoroutine;
    private bool isConnecting;
    private bool boundVideoTexture;
    private bool remoteDescriptionSet;
    private string sessionId;
    private string selectedProducerId;
    private int onVideoReceivedCount;
    private float lastOnVideoLogTime;

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

        if (holoOverlay != null)
            holoOverlay.SetActive(false);

#if !UNITY_EDITOR
        if (PlayerPrefs.HasKey($"{nameof(WebRTCClient)}_{gameObject.name}_SignalingUrl"))
            signalingUrl = PlayerPrefs.GetString($"{nameof(WebRTCClient)}_{gameObject.name}_SignalingUrl");
#endif

        if (webrtcUpdateCoroutine == null)
            webrtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
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

        if (!IsValidSignalingUrl(signalingUrl, out string normalizedUrl))
        {
            Debug.LogError($"[RecvUI] Invalid signaling URL: {signalingUrl}");
            return;
        }

        signalingUrl = normalizedUrl;
        isConnecting = true;
        boundVideoTexture = false;
        remoteDescriptionSet = false;
        sessionId = null;
        selectedProducerId = null;
        pendingRemoteIce.Clear();
        ShowVideoSurface(true);
        ConnectionStateChanged?.Invoke(true);

        ws = new WebSocket(signalingUrl)
        {
            EmitOnPing = true
        };

        ws.OnOpen += (_, _) => Debug.Log("[RecvUI][WS] Open");
        ws.OnMessage += (_, e) =>
        {
            string json = e.Data ?? Encoding.UTF8.GetString(e.RawData);
            mainThread.Enqueue(() => HandleSignalingMessage(json));
        };
        ws.OnError += (_, e) =>
        {
            Debug.LogError("[RecvUI][WS] Error: " + e.Message);
            mainThread.Enqueue(HandleSignalingStopped);
        };
        ws.OnClose += (_, e) =>
        {
            Debug.Log("[RecvUI][WS] Closed: " + e.Reason);
            mainThread.Enqueue(HandleSignalingStopped);
        };

        ws.ConnectAsync();
    }

    public void Disconnect()
    {
        Debug.Log("[RecvUI] Disconnect() requested");

        if (ws != null && ws.IsAlive && !string.IsNullOrEmpty(sessionId))
            SendJson(new JObject { ["type"] = "endSession", ["sessionId"] = sessionId });

        ClosePeerConnection();

        try
        {
            if (ws != null && ws.IsAlive)
                ws.CloseAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError("[RecvUI] WS CloseAsync error: " + ex);
        }
        finally
        {
            ws = null;
        }

        isConnecting = false;
        sessionId = null;
        selectedProducerId = null;
        pendingRemoteIce.Clear();
        ClearVideoSurface();
        ConnectionStateChanged?.Invoke(false);
    }

    public static bool IsValidSignalingUrl(string url, out string normalizedUrl)
    {
        normalizedUrl = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
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

    public void ToggleConnect()
    {
        if (ws != null && ws.IsAlive)
            Disconnect();
        else
            UserConnectWithAddress();
    }

    private void UserConnectWithAddress()
    {
#if !UNITY_EDITOR
        if (userInputAddress != null && !string.IsNullOrWhiteSpace(userInputAddress.text))
        {
            string rawInput = userInputAddress.text.Trim();
            if (!rawInput.Contains("://"))
                rawInput = $"ws://{rawInput}";

            if (!IsValidSignalingUrl(rawInput, out string normalizedUrl))
            {
                Debug.LogError("[RecvUI] Invalid signaling address.");
                return;
            }

            signalingUrl = normalizedUrl;
            PlayerPrefs.SetString($"{nameof(WebRTCClient)}_{gameObject.name}_SignalingUrl", signalingUrl);
            PlayerPrefs.Save();
        }
#endif

        Connect();
    }

    private void HandleSignalingMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        JObject message;
        try
        {
            message = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecvUI][WS] Ignoring invalid JSON: {ex.Message}");
            return;
        }

        string type = message.Value<string>("type");
        switch (type)
        {
            case "welcome":
                SendListenerStatus();
                SendJson(new JObject { ["type"] = "list" });
                break;
            case "list":
                StartFirstProducerSession(message);
                break;
            case "peerStatusChanged":
                if (string.IsNullOrEmpty(selectedProducerId))
                    SendJson(new JObject { ["type"] = "list" });
                break;
            case "sessionStarted":
                sessionId = message.Value<string>("sessionId");
                break;
            case "peer":
                HandlePeerMessage(message);
                break;
            case "endSession":
                HandleSignalingStopped();
                break;
        }
    }

    private void SendListenerStatus()
    {
        var msg = new JObject
        {
            ["type"] = "setPeerStatus",
            ["roles"] = new JArray("listener"),
            ["meta"] = new JObject
            {
                ["name"] = listenerName
            }
        };
        SendJson(msg);
    }

    private void StartFirstProducerSession(JObject message)
    {
        if (!string.IsNullOrEmpty(selectedProducerId))
            return;

        JArray producers = message["producers"] as JArray;
        if (producers == null || producers.Count == 0)
        {
            Debug.LogWarning("[RecvUI][WS] No GStreamer producers found.");
            return;
        }

        JObject selected = null;
        foreach (JObject producer in producers)
        {
            string producerName = producer["meta"]?.Value<string>("name");
            if (string.Equals(producerName, preferredProducerName, StringComparison.OrdinalIgnoreCase))
            {
                selected = producer;
                break;
            }
        }

        if (selected == null)
            selected = producers[0] as JObject;
        selectedProducerId = selected?.Value<string>("id");
        if (string.IsNullOrWhiteSpace(selectedProducerId))
        {
            Debug.LogWarning("[RecvUI][WS] Producer entry did not contain an id.");
            return;
        }

        SendJson(new JObject
        {
            ["type"] = "startSession",
            ["peerId"] = selectedProducerId
        });
    }

    private void HandlePeerMessage(JObject message)
    {
        string incomingSessionId = message.Value<string>("sessionId");
        if (!string.IsNullOrEmpty(incomingSessionId))
            sessionId = incomingSessionId;

        JObject sdp = message["sdp"] as JObject;
        if (sdp != null)
        {
            string sdpType = sdp.Value<string>("type");
            if (string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase))
            {
                string offerSdp = sdp.Value<string>("sdp");
                if (!string.IsNullOrWhiteSpace(offerSdp))
                    StartCoroutine(HandleOfferCoroutine(offerSdp));
            }
        }

        JObject ice = message["ice"] as JObject;
        if (ice != null)
            AddRemoteIceOrQueue(ice.ToObject<IceMsgUI>());
    }

    private IEnumerator HandleOfferCoroutine(string offerSdp)
    {
        EnsurePeerConnection();

        var offer = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = offerSdp
        };

        var setRemoteOp = pc.SetRemoteDescription(ref offer);
        yield return setRemoteOp;
        remoteDescriptionSet = true;

        FlushPendingRemoteIce();

        var answerOp = pc.CreateAnswer();
        yield return answerOp;

        var answer = answerOp.Desc;
        var setLocalOp = pc.SetLocalDescription(ref answer);
        yield return setLocalOp;

        SendJson(new JObject
        {
            ["type"] = "peer",
            ["sessionId"] = sessionId,
            ["sdp"] = new JObject
            {
                ["type"] = "answer",
                ["sdp"] = answer.sdp
            }
        });

        isConnecting = false;
        ConnectionStateChanged?.Invoke(true);
    }

    private void EnsurePeerConnection()
    {
        if (pc != null)
            return;

        var cfg = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        pc = new RTCPeerConnection(ref cfg);

        pc.OnConnectionStateChange = s => Debug.Log($"[RecvUI] ConnState: {s}");
        pc.OnIceCandidate = candidate =>
        {
            if (candidate == null || string.IsNullOrEmpty(sessionId))
                return;

            SendJson(new JObject
            {
                ["type"] = "peer",
                ["sessionId"] = sessionId,
                ["ice"] = new JObject
                {
                    ["candidate"] = candidate.Candidate,
                    ["sdpMid"] = candidate.SdpMid,
                    ["sdpMLineIndex"] = candidate.SdpMLineIndex ?? 0
                }
            });
        };
        pc.OnDataChannel = channel =>
        {
            dataChannel = channel;
            dataChannel.OnMessage = bytes =>
            {
                string message = Encoding.UTF8.GetString(bytes);
                Debug.Log($"[RecvUI] DataChannel: {message}");
            };
        };
        pc.OnTrack = e =>
        {
            Debug.Log($"[RecvUI] OnTrack kind={e.Track.Kind}");
            if (e.Track is VideoStreamTrack videoTrack)
            {
                videoTrack.OnVideoReceived += texture =>
                {
                    if (texture == null)
                        return;

                    onVideoReceivedCount++;
                    if (Time.time - lastOnVideoLogTime > 1f)
                    {
                        Debug.Log($"[RecvUI] OnVideoReceived callback rate ~ {onVideoReceivedCount}/sec");
                        onVideoReceivedCount = 0;
                        lastOnVideoLogTime = Time.time;
                    }

                    mainThread.Enqueue(() =>
                    {
                        if (boundVideoTexture)
                            return;

                        boundVideoTexture = true;
                        if (receiveRawImage != null)
                        {
                            receiveRawImage.texture = texture;
                            receiveRawImage.enabled = true;
                            Debug.Log($"[RecvUI] Bound texture once: {texture.width}x{texture.height} {texture.GetType()}");
                        }

                        if (holoOverlay != null)
                            holoOverlay.SetActive(true);
                    });
                };
            }
        };
    }

    private void AddRemoteIceOrQueue(IceMsgUI ice)
    {
        if (ice == null || string.IsNullOrEmpty(ice.candidate))
            return;

        if (pc == null || !remoteDescriptionSet)
        {
            pendingRemoteIce.Add(ice);
            return;
        }

        AddRemoteIce(ice);
    }

    private void FlushPendingRemoteIce()
    {
        foreach (var ice in pendingRemoteIce)
            AddRemoteIce(ice);
        pendingRemoteIce.Clear();
    }

    private void AddRemoteIce(IceMsgUI ice)
    {
        if (pc == null || ice == null)
            return;

        pc.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit
        {
            candidate = ice.candidate,
            sdpMid = ice.sdpMid,
            sdpMLineIndex = ice.sdpMLineIndex
        }));
    }

    private void SendJson(JObject message)
    {
        if (ws == null || !ws.IsAlive || message == null)
            return;

        ws.Send(message.ToString(Formatting.None));
    }

    private void HandleSignalingStopped()
    {
        isConnecting = false;
        ClosePeerConnection();
        ws = null;
        sessionId = null;
        selectedProducerId = null;
        pendingRemoteIce.Clear();
        ClearVideoSurface();
        ConnectionStateChanged?.Invoke(false);
    }

    private void ClosePeerConnection()
    {
        try
        {
            dataChannel?.Close();
            dataChannel = null;
            if (pc != null)
            {
                pc.Close();
                pc.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[RecvUI] PC cleanup failed: " + ex);
        }
        finally
        {
            pc = null;
            boundVideoTexture = false;
            remoteDescriptionSet = false;
        }
    }

    private void ShowVideoSurface(bool visible)
    {
        if (receiveRawImage == null)
            return;

        receiveRawImage.enabled = visible;
        receiveRawImage.color = Color.white;
    }

    private void ClearVideoSurface()
    {
        if (receiveRawImage != null)
        {
            receiveRawImage.texture = null;
            receiveRawImage.enabled = false;
        }

        if (holoOverlay != null)
            holoOverlay.SetActive(false);
    }

    private void Update()
    {
        while (mainThread.TryDequeue(out var action))
        {
            try { action?.Invoke(); }
            catch (Exception ex) { Debug.LogError(ex); }
        }
    }

    private void OnDestroy()
    {
        try
        {
            if (ws != null && ws.IsAlive)
                ws.CloseAsync();
        }
        catch
        {
        }

        ClosePeerConnection();
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
