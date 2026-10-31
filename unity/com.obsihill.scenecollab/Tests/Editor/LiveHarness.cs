using System;
using System.IO;
using System.Text;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The end-to-end check: one real Unity editor, one real server, one real second participant.
    ///
    /// It is deliberately not a [Test]. A live session needs the editor loop to keep running while it
    /// waits (the socket pump lives in <c>EditorApplication.update</c>), and a blocking test method
    /// starves that loop, so the test would fail on its own harness rather than on the code. This runs
    /// instead as <c>-executeMethod</c> without <c>-quit</c>: Unity keeps ticking, the state machine
    /// advances, and the process exits with 0 only when both directions actually moved a cube.
    ///
    /// Environment:
    ///   SCENECOLLAB_LIVE_URL    ws://127.0.0.1:8100/ws
    ///   SCENECOLLAB_LIVE_TOKEN  access token
    ///   SCENECOLLAB_LIVE_ROOM   room name
    ///   SCENECOLLAB_LIVE_DIR    directory the peer watches (artifacts/live)
    ///
    /// Exit codes: 0 both directions verified, 1 failed, 2 not configured (skipped).
    /// </summary>
    public static class LiveHarness
    {
        const string Folder = "Assets/__collab_live";
        const string ScenePath = Folder + "/Live.unity";

        static ISceneCollabSession _session;
        static SceneCollabSync _sync;
        static GameObject _cube;
        static string _nodeId;
        static string _dir;
        static double _stageDeadline;
        static int _stage;
        static bool _finished;
        static double _soakStart;
        static bool _reconnectMode;
        static bool _sawDrop;
        static bool _reconnectEditSent;
        static int _sentBeforeReconnect;

        [MenuItem("Tools/Scene Collab/Run live harness (needs a server)")]
        public static void RunFromMenu()
        {
            Run();
        }

        /// <summary>Batch-mode entry point. Never throws: an exception here hides the exit code.</summary>
        public static void Run()
        {
            try
            {
                Start();
            }
            catch (Exception error)
            {
                Fail("start: " + error);
            }
        }

        static void Start()
        {
            var url = Environment.GetEnvironmentVariable("SCENECOLLAB_LIVE_URL");
            var token = Environment.GetEnvironmentVariable("SCENECOLLAB_LIVE_TOKEN");
            var room = Environment.GetEnvironmentVariable("SCENECOLLAB_LIVE_ROOM");
            _dir = Environment.GetEnvironmentVariable("SCENECOLLAB_LIVE_DIR");

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(room) || string.IsNullOrEmpty(_dir))
            {
                Debug.Log("[live] not configured (SCENECOLLAB_LIVE_URL/ROOM/DIR); skipping");
                EditorApplication.Exit(2);
                return;
            }

            _reconnectMode = Environment.GetEnvironmentVariable("SCENECOLLAB_LIVE_RECONNECT") == "1";
            _dir = Path.GetFullPath(_dir);
            Directory.CreateDirectory(_dir);
            ResetArtifacts();

            _cube = BuildScene();
            if (_cube == null)
            {
                return;
            }

            CollabSettings.Token = token ?? "";

            var settings = new CollabSettings
            {
                serverUrl = url,
                room = room,
                displayName = "unity-live",
            };

            _sync = new SceneCollabSync();
            _session = new CollabSession();
            _sync.Bind(_session);
            _session.StateChanged += OnStateChanged;
            _session.ErrorReceived += (code, message) => Debug.LogWarning("[live] server error " + code + ": " + message);

            EditorApplication.update += Tick;
            Debug.Log("[live] connecting to " + settings.BuildConnectionUrl());
            _session.Connect(settings);

            _stage = 0;
            _stageDeadline = EditorApplication.timeSinceStartup + 60.0;
        }

        static GameObject BuildScene()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "__collab_live");
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Fail("no active scene");
                return null;
            }

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Live Cube";
            cube.transform.localPosition = Vector3.zero;
            SceneManager.MoveGameObjectToScene(cube, scene);

            // A node id only exists once the scene has an asset (the id is built from scene GUID +
            // fileID), so the scene is saved before anything is claimed about it.
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Fail("could not save " + ScenePath);
                return null;
            }

            Debug.Log("[live] scene saved to " + ScenePath);
            return cube;
        }

        static void OnStateChanged()
        {
            if (_session == null || _sync == null || _sync.Enabled)
            {
                return;
            }

            if (_session.State != CollabConnectionState.Connected)
            {
                return;
            }

            _sync.Enable();
            _nodeId = _sync.Registry.EnsureNodeId(_cube);

            if (string.IsNullOrEmpty(_nodeId))
            {
                Fail("the saved cube got no node id — the registry is broken, not the network");
                return;
            }

            WriteJson("unity-ready.json", "{\"nodeId\":\"" + _nodeId + "\",\"scenePath\":\"" + ScenePath
                + "\",\"clientId\":\"" + _session.ClientId + "\",\"isScribe\":" + (_session.IsScribe ? "true" : "false") + "}");

            Debug.Log("[live] connected as " + _session.ClientId + "; cube node " + _nodeId);
            _stage = 1;
            _stageDeadline = EditorApplication.timeSinceStartup + 60.0;
        }

        static void Tick()
        {
            if (_finished)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup > _stageDeadline)
            {
                Fail("stage " + _stage + " timed out; last note: " + (_sync == null ? "-" : _sync.LastNote));
                return;
            }

            if (_stage == 1)
            {
                if (_session == null || _session.State != CollabConnectionState.Connected)
                {
                    return;
                }

                var position = _cube.transform.localPosition;
                if ((position - new Vector3(3f, 4f, 5f)).sqrMagnitude > 0.0001f)
                {
                    return;
                }

                WriteJson("unity-applied.json", "{\"localPosition\":\"" + position.x + "," + position.y + "," + position.z
                    + "\",\"revision\":" + _session.Revision + ",\"applied\":" + _sync.Applied + "}");
                Debug.Log("[live] peer op applied: cube is at " + position + " (revision " + _session.Revision + ")");

                // Now make an edit of our own — through Undo, exactly like a user dragging in the
                // inspector, because that is what the capture layer listens to.
                Undo.RecordObject(_cube.transform, "live move");
                _cube.transform.localPosition = new Vector3(7f, 8f, 9f);
                EditorSceneManager.MarkSceneDirty(_cube.scene);

                _stage = 2;
                _stageDeadline = EditorApplication.timeSinceStartup + 60.0;
                return;
            }

            if (_stage == 2 && File.Exists(Path.Combine(_dir, "peer-observed.json")))
            {
                var observed = File.ReadAllText(Path.Combine(_dir, "peer-observed.json"));
                WriteJson("unity-result.json", "{\"ok\":true,\"sent\":" + _sync.Sent + ",\"applied\":" + _sync.Applied
                    + ",\"ignored\":" + _sync.Ignored + ",\"rejected\":" + _sync.Rejected
                    + ",\"peerObserved\":" + observed.Trim() + "}");
                Debug.Log("[live] both directions verified. sent=" + _sync.Sent + " applied=" + _sync.Applied);

                // Hold the session open past the server's silence window with nobody typing. Before
                // the heartbeat, an idle editor stayed "connected" long after the socket was dead;
                // now an unanswered ping is a hang-up, so this stage fails loudly if the editor
                // forgets to reply.
                if (_reconnectMode)
                {
                    // The shell kills the server now and brings it back a few seconds later; the
                    // editor is expected to notice on its own and come back without a click.
                    _stage = 5;
                    _stageDeadline = EditorApplication.timeSinceStartup + 240.0;
                    return;
                }

                _soakStart = EditorApplication.timeSinceStartup;
                _stage = 3;
                _stageDeadline = _soakStart + 180.0;
                return;
            }

            if (_stage == 5)
            {
                if (_session == null)
                {
                    return;
                }

                if (_session.State != CollabConnectionState.Connected)
                {
                    if (!_sawDrop)
                    {
                        _sawDrop = true;
                        _sentBeforeReconnect = _sync.Sent;
                        Debug.Log("[live] connection dropped (" + _session.State + "); waiting for the retry");
                    }

                    return;
                }

                if (!_sawDrop)
                {
                    return;
                }

                if (!_reconnectEditSent)
                {
                    Debug.Log("[live] reconnected on its own; editing again");
                    Undo.RecordObject(_cube.transform, "live reconnect");
                    _cube.transform.localPosition = new Vector3(4f, 5f, 6f);
                    EditorSceneManager.MarkSceneDirty(_cube.scene);
                    _reconnectEditSent = true;
                    _stageDeadline = EditorApplication.timeSinceStartup + 60.0;
                    return;
                }

                if (_sync.Sent <= _sentBeforeReconnect)
                {
                    return;
                }

                if (_sync.Rejected > 0)
                {
                    Fail("the edit after the reconnect was rejected (" + _sync.Rejected + ")");
                    return;
                }

                WriteJson("unity-reconnect.json", "{\"ok\":true,\"dropped\":true,\"state\":\""
                    + _session.State + "\",\"revision\":" + _session.Revision
                    + ",\"sentBefore\":" + _sentBeforeReconnect + ",\"sentAfter\":" + _sync.Sent
                    + ",\"clientId\":\"" + _session.ClientId + "\"}");
                Debug.Log("[live] PASS: the editor dropped, came back, and its next edit went out");
                Finish(0);
                return;
            }

            if (_stage == 3)
            {
                var held = EditorApplication.timeSinceStartup - _soakStart;
                if (held < 75.0)
                {
                    return;
                }

                if (_session == null || _session.State != CollabConnectionState.Connected)
                {
                    Fail("the session did not survive 75 s idle (state "
                        + (_session == null ? "null" : _session.State.ToString()) + ")");
                    return;
                }

                Debug.Log("[live] held the session for " + held.ToString("0") + " s idle; editing again");
                Undo.RecordObject(_cube.transform, "live soak");
                _cube.transform.localPosition = new Vector3(1f, 2f, 3f);
                EditorSceneManager.MarkSceneDirty(_cube.scene);

                _stage = 4;
                _stageDeadline = EditorApplication.timeSinceStartup + 120.0;
                return;
            }

            if (_stage == 4 && File.Exists(Path.Combine(_dir, "peer-soak.json")))
            {
                var held = EditorApplication.timeSinceStartup - _soakStart;
                var observed = File.ReadAllText(Path.Combine(_dir, "peer-soak.json"));
                WriteJson("unity-soak.json", "{\"ok\":true,\"idleSeconds\":" + held.ToString("0.0")
                    + ",\"state\":\"" + _session.State + "\",\"revision\":" + _session.Revision
                    + ",\"peerObserved\":" + observed.Trim() + "}");
                Debug.Log("[live] PASS: the session outlived the silence window and still relays edits ("
                    + held.ToString("0") + " s idle)");
                Finish(0);
            }
        }

        static void Fail(string reason)
        {
            Debug.LogError("[live] FAIL: " + reason);
            WriteJson("unity-failure.json", "{\"ok\":false,\"reason\":\"" + reason.Replace("\"", "'") + "\"}");
            Finish(1);
        }

        static void Finish(int exitCode)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            EditorApplication.update -= Tick;

            if (_sync != null)
            {
                _sync.Disable();
                _sync.Dispose();
            }

            if (_session != null)
            {
                _session.StateChanged -= OnStateChanged;
                _session.Disconnect();
            }

            EditorApplication.Exit(exitCode);
        }

        static void ResetArtifacts()
        {
            string[] stale =
            {
                "unity-ready.json", "unity-applied.json", "unity-result.json", "unity-failure.json",
                "peer-observed.json", "peer-sent.json", "peer-ready.json",
            };

            for (var i = 0; i < stale.Length; i++)
            {
                var path = Path.Combine(_dir, stale[i]);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        static void WriteJson(string fileName, string json)
        {
            var path = Path.Combine(_dir, fileName);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Debug.Log("[live] wrote " + path + ": " + json);
        }
    }
}
