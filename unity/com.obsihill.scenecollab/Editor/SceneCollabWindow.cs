using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Editor.SceneSync;

namespace SceneCollab.Editor
{
    /// <summary>
    /// The whole UI: connect, see who is here, watch the event log.
    ///
    /// Intentionally small. Anything that could be a setting lives in
    /// <see cref="CollabSettings"/>; anything that could be a feature lives behind
    /// <see cref="ISceneCollabSession"/>. A tool window that tries to be a dashboard is a
    /// tool window people stop opening.
    /// </summary>
    public sealed class SceneCollabWindow : EditorWindow
    {
        const int MaxLogEntries = 60;

        readonly List<string> _log = new List<string>();
        CollabSettings _settings;
        ISceneCollabSession _session;
        SceneCollabSync _sync;
        readonly CollabRoomList _roomList = new CollabRoomList();
        Vector2 _logScroll;
        Vector2 _bodyScroll;
        string _tokenDraft;
        string _tokenNotice;
        bool _roomsDirty;
        double _nextStatusRepaint;

        [MenuItem("Tools/Scene Collab", priority = 40)]
        public static void Open()
        {
            var window = GetWindow<SceneCollabWindow>("Scene Collab");
            window.minSize = new Vector2(340f, 320f);
            window.Show();
        }

        void OnEnable()
        {
            _settings = CollabSettings.Load();
            _tokenDraft = CollabSettings.Token;
            Attach(new CollabSession());
            EditorApplication.update += PollRooms;
        }

        void OnDisable()
        {
            EditorApplication.update -= PollRooms;
            _roomList.Cancel();
            Detach();
        }

        void PollRooms()
        {
            if (EditorApplication.timeSinceStartup >= _nextStatusRepaint)
            {
                _nextStatusRepaint = EditorApplication.timeSinceStartup + 0.25;
                Repaint();
            }
            if (!_roomList.Poll())
            {
                return;
            }

            _roomsDirty = true;
            Repaint();

            if (!string.IsNullOrEmpty(_roomList.Error))
            {
                Log("room list: " + _roomList.Error);
            }
        }
        void Attach(ISceneCollabSession session)
        {
            _session = session;
            _session.StateChanged += Repaint;
            _session.StateChanged += OnStateChanged;
            _session.ParticipantsChanged += Repaint;
            _session.OperationReceived += OnOperationReceived;
            _session.ErrorReceived += OnError;

            if (_sync == null)
            {
                _sync = new SceneCollabSync();
            }

            _sync.Bind(session);
        }

        void Detach()
        {
            if (_session == null)
            {
                return;
            }

            _sync.Disable();
            _sync.Unbind();

            _session.StateChanged -= Repaint;
            _session.StateChanged -= OnStateChanged;
            _session.ParticipantsChanged -= Repaint;
            _session.OperationReceived -= OnOperationReceived;
            _session.ErrorReceived -= OnError;
            _session.Disconnect();
            _session = null;
        }

        void OnStateChanged()
        {
            if (_session == null)
            {
                return;
            }

            if (_session.State == CollabConnectionState.Connected)
            {
                Log("connected as " + _session.ClientId + (_session.IsHost ? " (host)" : "")
                    + " · revision " + _session.Revision);
            }
            else if (_session.State == CollabConnectionState.Faulted)
            {
                Log("connection failed");
            }
        }

        void OnOperationReceived(WireMessage message)
        {
            Log("op " + message.opKind + " from " + message.displayName + " @" + message.revision);
        }

        void OnError(string code, string message)
        {
            Log("! " + code + ": " + (string.IsNullOrEmpty(message) ? "(no detail)" : message));
        }

        void Log(string line)
        {
            _log.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (_log.Count > MaxLogEntries)
            {
                _log.RemoveAt(_log.Count - 1);
            }

            Repaint();
        }

        void OnGUI()
        {
            if (_settings == null || _session == null)
            {
                EditorGUILayout.HelpBox("Reloading…", MessageType.Info);
                return;
            }

            var connected = _session.State == CollabConnectionState.Connected;
            var connecting = _session.State == CollabConnectionState.Connecting;
            var editable = !connected && !connecting;
            _bodyScroll = EditorGUILayout.BeginScrollView(_bodyScroll);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!editable))
            {
                _settings.serverUrl = EditorGUILayout.TextField("Address", _settings.serverUrl);
                _settings.room = EditorGUILayout.TextField("Room", _settings.room);
                _settings.displayName = EditorGUILayout.TextField("Your name", _settings.displayName);
                _settings.errorModelPath = ErrorModelField(_settings.errorModelPath);
                _tokenDraft = EditorGUILayout.PasswordField("Token", _tokenDraft);
            }

            if (!string.IsNullOrEmpty(_tokenNotice))
            {
                EditorGUILayout.HelpBox(_tokenNotice, MessageType.Warning);
            }

            EditorGUILayout.Space(4f);
            DrawRooms(editable, connected, connecting);

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (connected || connecting)
                {
                    if (GUILayout.Button(connecting ? "Cancel" : "Disconnect", GUILayout.Height(24f)))
                    {
                        Detach();
                        Attach(new CollabSession());
                        Log("disconnected");
                    }
                }
                else if (GUILayout.Button("Connect", GUILayout.Height(24f)))
                {
                    ConnectPressed();
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("State", _session.State.ToString());
            EditorGUILayout.LabelField("Client", string.IsNullOrEmpty(_session.ClientId) ? "—" : _session.ClientId);
            EditorGUILayout.LabelField("Revision", _session.Revision.ToString());
            EditorGUILayout.LabelField("Role", _session.IsHost
                ? "host — this editor saves the scene"
                : (_session.IsScribe ? "scribe" : "guest"));
            EditorGUILayout.LabelField("Scene", string.IsNullOrEmpty(_session.SceneName)
                ? "—"
                : _session.SceneName + (string.IsNullOrEmpty(_session.SceneGuid) ? "" : "  [" + ShortGuid(_session.SceneGuid) + "]"));

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Scene sync", EditorStyles.boldLabel);
            var syncing = _sync != null && _sync.Enabled;
            if (syncing && GUILayout.Button("Stop collaboration / disconnect", GUILayout.Height(22f)))
            {
                Detach();
                Attach(new CollabSession());
                Log("collaboration stopped");
                GUIUtility.ExitGUI();
            }

            if (_sync != null)
            {
                DrawRecovery();
                EditorGUILayout.LabelField("Known nodes", _sync.KnownNodes.ToString());
                EditorGUILayout.LabelField("Structure sent", _sync.StructureSent.ToString());
                EditorGUILayout.LabelField("Held by lock", _sync.LocksSkipped.ToString());
                EditorGUILayout.LabelField("Sent / rejected", _sync.Sent + " / " + _sync.Rejected);
                EditorGUILayout.LabelField("Applied / ignored", _sync.Applied + " / " + _sync.Ignored);

                if (_sync.MissingReferences > 0)
                {
                    EditorGUILayout.LabelField("Unresolved refs", _sync.MissingReferences.ToString(),
                        EditorStyles.boldLabel);
                }

                if (_sync.UnsupportedValues > 0)
                {
                    EditorGUILayout.LabelField("Unsupported values", _sync.UnsupportedValues.ToString(),
                        EditorStyles.boldLabel);
                }
                if (_sync.HostVerifying)
                {
                    EditorGUILayout.HelpBox(
                        "This editor was just promoted to host and is still catching up (" + _sync.HostStateNote
                        + "). Saving is held until the room goes quiet, so what reaches Git is the whole "
                        + "session and not the part that had arrived.",
                        MessageType.Warning);
                }

                if (_sync.UndoRefused > 0)
                {
                    // Ctrl+Z is the one local edit that can reach into someone else's change. Saying how
                    // much of it was kept is the difference between a baffling partial undo and a rule
                    // the user can predict.
                    EditorGUILayout.HelpBox(
                        "Your last undo restored " + _sync.UndoPublished + " value(s) and left "
                        + _sync.UndoRefused + " alone: another editor changed those after you did, so the "
                        + "room's value was kept.",
                        MessageType.Info);
                }

                EditorGUILayout.LabelField("Last", string.IsNullOrEmpty(_sync.LastNote) ? "—" : _sync.LastNote);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var showCameras = EditorGUILayout.ToggleLeft("Show peer cameras", _sync.Presence.Show);
                    if (showCameras != _sync.Presence.Show)
                    {
                        _sync.Presence.Show = showCameras;
                        SceneView.RepaintAll();
                    }

                    EditorGUILayout.LabelField(_sync.Presence.PeerCount + " camera(s)", GUILayout.Width(90f));
                }

                if (_sync.Terrain.KnownTerrains > 0)
                {
                    var terrain = _sync.Terrain;
                    EditorGUILayout.LabelField("Terrain",
                        terrain.KnownTerrains + " terrain(s) — "
                        + (_sync.CanEdit ? "automatic region locks" : "editing paused"));
                    EditorGUILayout.LabelField("Terrain sent / applied", terrain.Sent + " / " + terrain.Applied);
                    EditorGUILayout.LabelField(terrain.LastNote ?? "", EditorStyles.wordWrappedMiniLabel);
                }

                if (_sync.Terrain.Skipped > 0)
                {
                    EditorGUILayout.HelpBox(
                        _sync.Terrain.Skipped + " terrain patch(es) could not be applied"
                        + (string.IsNullOrEmpty(_sync.Terrain.LastNote) ? "." : ": " + _sync.Terrain.LastNote + "."),
                        MessageType.Warning);
                }

                if (!string.IsNullOrEmpty(_sync.AssetNote))
                {
                    EditorGUILayout.HelpBox(_sync.AssetNote + " — ERROR signs mark what this "
                        + "checkout cannot resolve. They are not saved. The reason is in each box's "
                        + "name (" + SceneCollab.Editor.SceneSync.CollabPlaceholders.MarkerName
                        + " …) in the hierarchy.", MessageType.Warning);
                }

                if (_sync.CanEdit)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Lock selection"))
                        {
                            SendLockOnSelection(true);
                        }

                        if (GUILayout.Button("Unlock selection"))
                        {
                            SendLockOnSelection(false);
                        }
                    }

                    if (_session.IsHost && GUILayout.Button("Publish asset manifest"))
                    {
                        _sync.PublishManifest();
                        Log(_sync.LastNote);
                    }
                }

                if (_sync.Overwritten > 0)
                {
                    EditorGUILayout.HelpBox(
                        _sync.Overwritten + " remote change(s) landed on a value you had also changed. "
                        + "Scalars are last-writer-wins; lock an object before restructuring it.",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Participants (" + _session.Participants.Count + ")", EditorStyles.boldLabel);

            var participants = _session.Participants;
            for (var i = 0; i < participants.Count; i++)
            {
                var participant = participants[i];
                var label = participant.displayName + (participant.isScribe ? "  ★" : "");
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawColorSwatch(participant.color);
                    EditorGUILayout.LabelField(label);
                }
            }

            if (participants.Count == 0)
            {
                EditorGUILayout.LabelField("nobody yet", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(90f));
            for (var i = 0; i < _log.Count; i++)
            {
                EditorGUILayout.LabelField(_log[i], EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndScrollView();
        }

        void DrawRooms(bool editable, bool connected, bool connecting)
        {
            EditorGUILayout.LabelField("Scenes on this server", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_roomList.Busy))
                {
                    if (GUILayout.Button(_roomList.Busy ? "Loading…" : "Refresh"))
                    {
                        _roomList.Refresh(_settings.serverUrl, CollabSettings.Token);
                    }
                }

                if (!string.IsNullOrEmpty(_roomList.Error))
                {
                    EditorGUILayout.LabelField(_roomList.Error, EditorStyles.miniLabel);
                }
            }

            var rooms = _roomList.Rooms;
            if (rooms.Count == 0)
            {
                EditorGUILayout.LabelField("nobody is hosting a scene right now", EditorStyles.miniLabel);
                return;
            }

            for (var i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                var same = CollabRoomList.MatchesActiveScene(room);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var label = (string.IsNullOrEmpty(room.sceneName) ? "(unnamed scene)" : room.sceneName)
                        + " · " + room.room
                        + " · " + (string.IsNullOrEmpty(room.host) ? "?" : room.host)
                        + " · " + room.participants + " here"
                        + (same ? "" : "  [other scene]");

                    EditorGUILayout.LabelField(label, EditorStyles.miniLabel);

                    using (new EditorGUI.DisabledScope(!editable || !same))
                    {
                        if (GUILayout.Button(connected ? "join" : "join", GUILayout.Width(48f)))
                        {
                            _settings.room = room.room;
                            _settings.Save();
                            Log("room set to " + room.room + " — press Connect");
                        }
                    }
                }
            }
        }

        void SendLockOnSelection(bool acquire)
        {
            var selection = Selection.gameObjects;
            var sent = 0;
            for (var i = 0; i < selection.Length; i++)
            {
                string nodeId;
                if (_sync.Registry.TryGetNodeId(selection[i], out nodeId) && !string.IsNullOrEmpty(nodeId))
                {
                    _session.SendLock(nodeId, !acquire);
                    sent++;
                }
            }

            Log("requested " + sent + (acquire ? " lock(s)" : " unlock(s)"));
        }

        void DrawRecovery()
        {
            if (!_sync.Enabled) return;
            EditorGUILayout.HelpBox(_sync.RecoveryStatus ?? "동기화 준비 중",
                _sync.CanEdit ? MessageType.Info : MessageType.Warning);
            var total = _sync.RecoverySceneTotal;
            if (total > 0 && !_sync.CanEdit)
            {
                var done = _sync.RecoverySceneCompleted;
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(rect, Mathf.Clamp01((float)done / total), "장면 " + done + " / " + total);
            }
            if (!_sync.CanEdit)
                EditorGUILayout.LabelField("터레인 처리", _sync.RecoveryTerrainTiles + " 타일");
            if (!_sync.CanEdit && _sync.RecoveryTerrainTotal > 0)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(rect, Mathf.Clamp01((float)_sync.RecoveryTerrainTiles / _sync.RecoveryTerrainTotal),
                    "터레인 " + _sync.RecoveryTerrainTiles + " / " + _sync.RecoveryTerrainTotal);
            }
            var session = _session as CollabSession;
            if (session != null && _session.State == CollabConnectionState.Connecting && session.RetryAt > 0)
                EditorGUILayout.LabelField("자동 재연결", Math.Max(0, (int)Math.Ceiling(session.RetryAt - EditorApplication.timeSinceStartup)) + "초 후");
            if (!string.IsNullOrEmpty(_sync.LastFailure))
            {
                EditorGUILayout.HelpBox((_sync.CanEdit ? "지난 실패 기록 (현재 복구 완료): " : "") + _sync.LastFailure,
                    _sync.CanEdit ? MessageType.Info : MessageType.Error);
                if (GUILayout.Button("실패 메시지 지우기")) _sync.ClearFailure();
            }
            using (new EditorGUI.DisabledScope(_session.State != CollabConnectionState.Connected))
            {
                if (GUILayout.Button("전체 재동기화 / 복구")) _sync.RetryRecovery();
            }
            if (!_sync.CanEdit)
                EditorGUILayout.LabelField("모든 참여자의 장면·터레인 검증이 끝나면 편집을 재개합니다.", EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>
        /// The prefab drawn at a missing object's place. Stored as an asset path, because the settings file is
        /// JSON and JSON cannot hold a reference. Empty falls back to the built-in ERROR sign.
        /// </summary>
        static string ErrorModelField(string current)
        {
            var prefab = string.IsNullOrEmpty(current) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(current);
            var picked = (GameObject)EditorGUILayout.ObjectField("ERROR model", prefab, typeof(GameObject), false);
            if (picked == prefab)
            {
                return current;
            }

            if (picked == null)
            {
                return "";
            }

            var path = AssetDatabase.GetAssetPath(picked);
            return string.IsNullOrEmpty(path) ? "" : path;
        }

        static string ShortGuid(string guid)
        {
            return string.IsNullOrEmpty(guid) || guid.Length <= 8 ? guid : guid.Substring(0, 8);
        }

        void ConnectPressed()
        {
            _tokenNotice = null;
            _settings.serverUrl = CollabSettings.Normalize(_settings.serverUrl);
            _settings.displayName = (_settings.displayName ?? "").Trim();

            if (_settings.displayName.Length == 0)
            {
                _settings.displayName = Environment.UserName;
            }

            CollabSettings.Token = _tokenDraft;
            _settings.Save();

            if (!CollabSettings.HasToken)
            {
                _tokenNotice = "No token set. The server refuses connections unless it was started "
                    + "with --insecure-dev for local testing.";
            }

            Log("connecting to " + _settings.BuildConnectionUrl());
            _session.Connect(_settings);
            _sync.Enable(); // Drain queued network verdicts before the sync layer checks ACK timeouts.
        }

        static void DrawColorSwatch(string hex)
        {
            Color color;
            if (string.IsNullOrEmpty(hex) || !ColorUtility.TryParseHtmlString(hex, out color))
            {
                color = new Color(0.5f, 0.5f, 0.5f);
            }

            var previous = GUI.color;
            GUI.color = color;
            GUILayout.Box("", GUILayout.Width(10f), GUILayout.Height(10f));
            GUI.color = previous;
        }
    }
}
