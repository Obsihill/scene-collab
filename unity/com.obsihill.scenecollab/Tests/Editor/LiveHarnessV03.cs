using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The host half of the v0.3 round trip: this editor opens and saves a scene, a guest that is not
    /// Unity creates an object in it, and the object has to appear here, move, and disappear again.
    ///
    /// Like <see cref="LiveHarness"/> this runs as <c>-executeMethod</c> without <c>-quit</c>, because
    /// the socket pump and the change feed both need the editor loop to keep ticking.
    ///
    /// Environment: SCENECOLLAB_LIVE_URL / _TOKEN / _ROOM / _DIR.
    /// Exit codes: 0 verified, 1 failed, 2 not configured.
    /// </summary>
    public static class LiveHarnessV03
    {
        const string Folder = "Assets/__collab_live";
        const string ScenePath = Folder + "/V03Host.unity";

        static ISceneCollabSession _session;
        static SceneCollabSync _sync;
        static GameObject _cube;
        static string _dir;
        static double _deadline;
        static int _stage;
        static bool _finished;
        static string _guestNodeId;

        [MenuItem("Tools/Scene Collab/Run v0.3 structural harness (needs a server)")]
        public static void RunFromMenu()
        {
            Run();
        }

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
                Debug.Log("[v03] not configured; skipping");
                EditorApplication.Exit(2);
                return;
            }

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
                displayName = "unity-host",
            };

            _sync = new SceneCollabSync();
            _session = new CollabSession();
            _sync.Bind(_session);
            _session.StateChanged += OnStateChanged;
            _session.ErrorReceived += (code, message) => Debug.LogWarning("[v03] server error " + code + ": " + message);

            EditorApplication.update += Tick;
            Debug.Log("[v03] connecting to " + settings.BuildConnectionUrl());
            _session.Connect(settings);

            _stage = 0;
            _deadline = EditorApplication.timeSinceStartup + 90.0;
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
            cube.name = "Host Cube";
            cube.transform.localPosition = new Vector3(1f, 1f, 1f);
            SceneManager.MoveGameObjectToScene(cube, scene);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Fail("could not save " + ScenePath);
                return null;
            }

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
            _sync.Registry.EnsureNodeId(_cube);

            // The guest reads this to join *this* scene: the guid is the whole handshake.
            var identity = SceneIdentity.CaptureActive();
            WriteJson("v03-host.json", "{\"sceneGuid\":\"" + identity.guid + "\",\"sceneName\":\"" + identity.name
                + "\",\"scenePath\":\"" + identity.path + "\",\"clientId\":\"" + _session.ClientId
                + "\",\"isHost\":" + (_session.IsHost ? "true" : "false") + "}");

            if (!_session.IsHost)
            {
                Fail("this editor joined second, so it is not the host — clear the room for this test");
                return;
            }

            // The host owns the manifest; the guest gets the same list in its welcome.
            _sync.PublishManifest();

            _stage = 1;
            _deadline = EditorApplication.timeSinceStartup + 90.0;
            Debug.Log("[v03] hosting scene " + identity.name + " as " + _session.ClientId);
        }

        static void Tick()
        {
            if (_finished)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup > _deadline)
            {
                Fail("stage " + _stage + " timed out; note: " + (_sync == null ? "-" : _sync.LastNote));
                return;
            }

            if (_stage == 1)
            {
                var guest = FindByName("Guest Cube");
                if (guest == null)
                {
                    return;
                }

                // The object arrived over the wire; now the id the guest invented has to resolve to it.
                var addedFile = Path.Combine(_dir, "v03-guest-added.json");
                if (File.Exists(addedFile))
                {
                    _guestNodeId = ExtractField(File.ReadAllText(addedFile), "nodeId");
                }

                GameObject resolved;
                var bound = !string.IsNullOrEmpty(_guestNodeId)
                    && _sync.Registry.TryResolve(_guestNodeId, out resolved)
                    && resolved == guest;
                Check(bound, "the guest's node id resolves to the object");

                var position = guest.transform.localPosition;
                if ((position - new Vector3(2f, 3f, 4f)).sqrMagnitude > 0.0001f)
                {
                    return;
                }

                WriteJson("v03-host-saw.json", "{\"name\":\"" + guest.name + "\",\"localPosition\":\""
                    + position.x + "," + position.y + "," + position.z + "\",\"revision\":" + _session.Revision
                    + ",\"applied\":" + _sync.Applied + ",\"nodeIdBound\":" + (bound ? "true" : "false") + "}");

                Debug.Log("[v03] guest object present at " + position + "; waiting for a component");
                _stage = 3;
                _deadline = EditorApplication.timeSinceStartup + 90.0;
                return;
            }

            // Stage 3/4: the guest adds and then removes a component. Neither fires a property
            // modification or a hierarchy change, so if the component signature sweep is broken the
            // host simply sits here until the deadline.
            if (_stage == 3)
            {
                var target = FindByName("Guest Cube");
                if (target == null)
                {
                    return;
                }

                if (target.GetComponent<AudioSource>() == null)
                {
                    return;
                }

                WriteJson("v03-host-comp.json", "{\"component\":\"AudioSource\",\"applied\":" + _sync.Applied
                    + ",\"note\":\"" + (_sync.LastNote ?? "") + "\"}");
                Debug.Log("[v03] guest component arrived: " + _sync.LastNote);
                _stage = 4;
                _deadline = EditorApplication.timeSinceStartup + 90.0;
                return;
            }

            if (_stage == 4)
            {
                var target = FindByName("Guest Cube");
                if (target == null)
                {
                    return;
                }

                if (target.GetComponent<AudioSource>() != null)
                {
                    return;
                }

                WriteJson("v03-host-comp-removed.json", "{\"component\":\"AudioSource\",\"removed\":true,\"applied\":"
                    + _sync.Applied + ",\"note\":\"" + (_sync.LastNote ?? "") + "\"}");
                Debug.Log("[v03] guest component removed: " + _sync.LastNote);
                _stage = 5;
                _deadline = EditorApplication.timeSinceStartup + 90.0;
                return;
            }

            if (_stage == 5)
            {
                if (FindByName("Guest Cube") != null)
                {
                    return;
                }

                WriteJson("v03-host-removed.json", "{\"removed\":true,\"applied\":" + _sync.Applied
                    + ",\"structureSent\":" + _sync.StructureSent + ",\"rejected\":" + _sync.Rejected + "}");
                WriteJson("v03-host-result.json", "{\"ok\":true,\"applied\":" + _sync.Applied
                    + ",\"structureSent\":" + _sync.StructureSent + ",\"locksSkipped\":" + _sync.LocksSkipped
                    + ",\"rejected\":" + _sync.Rejected + ",\"assetNote\":\"" + (_sync.AssetNote ?? "") + "\"}");
                Debug.Log("[v03] PASS: guest object created, moved, given a component, stripped of it and removed");
                Finish(0);
            }
        }

        static void Check(bool condition, string what)
        {
            if (!condition)
            {
                Debug.LogWarning("[v03] check failed: " + what);
            }
        }

        static GameObject FindByName(string name)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return null;
            }

            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name)
                {
                    return roots[i];
                }
            }

            return null;
        }

        static string ExtractField(string json, string field)
        {
            var marker = "\"" + field + "\":\"";
            var start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }

        static void Fail(string reason)
        {
            Debug.LogError("[v03] FAIL: " + reason);
            WriteJson("v03-host-failure.json", "{\"ok\":false,\"reason\":\"" + reason.Replace("\"", "'") + "\"}");
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
                "v03-host.json", "v03-host-saw.json", "v03-host-comp.json", "v03-host-comp-removed.json",
                "v03-host-removed.json", "v03-host-result.json",
                "v03-host-failure.json", "v03-guest-added.json", "v03-guest-result.json",
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
            Debug.Log("[v03] wrote " + path + ": " + json);
        }
    }
}
