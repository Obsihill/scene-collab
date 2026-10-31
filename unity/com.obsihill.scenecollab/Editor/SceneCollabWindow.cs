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
        Vector2 _logScroll;
        string _tokenDraft;
        string _tokenNotice;

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
        }

        void OnDisable()
        {
            Detach();
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
                Log("connected as " + _session.ClientId + (_session.IsScribe ? " (scribe)" : "")
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

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!editable))
            {
                _settings.serverUrl = EditorGUILayout.TextField("Address", _settings.serverUrl);
                _settings.room = EditorGUILayout.TextField("Room", _settings.room);
                _settings.displayName = EditorGUILayout.TextField("Your name", _settings.displayName);
                _tokenDraft = EditorGUILayout.PasswordField("Token", _tokenDraft);
            }

            if (!string.IsNullOrEmpty(_tokenNotice))
            {
                EditorGUILayout.HelpBox(_tokenNotice, MessageType.Warning);
            }

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
            EditorGUILayout.LabelField("Scribe", _session.IsScribe ? "this editor" : "another participant");

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Scene sync", EditorStyles.boldLabel);
            var syncing = _sync != null && _sync.Enabled;
            if (GUILayout.Button(syncing ? "Stop syncing" : "Start syncing", GUILayout.Height(22f)))
            {
                if (syncing)
                {
                    _sync.Disable();
                }
                else
                {
                    _sync.Enable();
                }
            }

            if (_sync != null)
            {
                EditorGUILayout.LabelField("Known nodes", _sync.KnownNodes.ToString());
                EditorGUILayout.LabelField("Sent / rejected", _sync.Sent + " / " + _sync.Rejected);
                EditorGUILayout.LabelField("Applied / ignored", _sync.Applied + " / " + _sync.Ignored);
                EditorGUILayout.LabelField("Last", string.IsNullOrEmpty(_sync.LastNote) ? "—" : _sync.LastNote);

                if (_sync.Overwritten > 0)
                {
                    EditorGUILayout.HelpBox(
                        _sync.Overwritten + " remote change(s) landed on a value you had also changed. "
                        + "Last writer wins on scalars; object-level locks are the fix and arrive in v0.3.",
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
