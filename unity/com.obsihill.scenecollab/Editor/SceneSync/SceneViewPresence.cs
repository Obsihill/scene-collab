using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Draws every other editor's SceneView camera as a small camera body with its frustum, and pushes
    /// this editor's own pose out about ten times a second.
    ///
    /// This is presence, not scene data: no GameObject is created, nothing is written to the scene, and
    /// nothing here is saved or synced as an op. Turn it off and the room simply stops drawing avatars.
    /// </summary>
    public sealed class SceneViewPresence
    {
        const double SendIntervalSeconds = 0.1;

        /// <summary>Idle editors still heartbeat so their avatar does not flicker out.</summary>
        const double HeartbeatSeconds = 2.0;

        /// <summary>A pose older than this is dropped: the peer either left or stopped sending.</summary>
        const double StaleSeconds = 5.0;

        const float MoveEpsilon = 0.01f;
        const float RotateEpsilonDegrees = 0.25f;
        const float FovEpsilon = 0.05f;

        sealed class PeerView
        {
            public string ClientId;
            public string DisplayName;
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov;
            public Color Color;
            public double LastSeen;
        }

        readonly Dictionary<string, PeerView> _peers = new Dictionary<string, PeerView>(StringComparer.Ordinal);

        ISceneCollabSession _session;
        double _nextSend;
        double _lastSentAt;
        double _lastSendAttempt;
        bool _hasLastSent;
        Vector3 _lastPosition;
        Quaternion _lastRotation;
        float _lastFov;

        /// <summary>Show the other editors' cameras in the SceneView.</summary>
        public bool Show { get; set; }

        public int PeerCount
        {
            get { return _peers.Count; }
        }

        public SceneViewPresence()
        {
            Show = true;
        }

        public void Bind(ISceneCollabSession session)
        {
            Unbind();
            _session = session;

            if (_session != null)
            {
                _session.ViewReceived += OnViewReceived;
            }

            SceneView.duringSceneGui += OnSceneGui;
        }

        public void Unbind()
        {
            if (_session != null)
            {
                _session.ViewReceived -= OnViewReceived;
            }

            _session = null;
            _peers.Clear();
            SceneView.duringSceneGui -= OnSceneGui;
        }

        /// <summary>Called from the sync tick; cheap when the camera has not moved.</summary>
        public void Tick()
        {
            if (_session == null)
            {
                return;
            }

            if (_session.State != CollabConnectionState.Connected)
            {
                _peers.Clear();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            PruneStale(now);

            if (now < _nextSend)
            {
                return;
            }

            _nextSend = now + SendIntervalSeconds;
            SendOwnView(now);
        }

        void SendOwnView(double now)
        {
            if (!Show)
            {
                return;
            }

            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null)
            {
                return;
            }

            var transform = view.camera.transform;
            var position = transform.position;
            var rotation = transform.rotation;
            var fov = view.camera.fieldOfView;

            var steady = _hasLastSent
                && (position - _lastPosition).sqrMagnitude < MoveEpsilon * MoveEpsilon
                && Quaternion.Angle(rotation, _lastRotation) < RotateEpsilonDegrees
                && Mathf.Abs(fov - _lastFov) < FovEpsilon;

            // Ten times a second of identical poses is wasted bandwidth on every peer; the heartbeat keeps
            // the avatar alive instead.
            if (steady && now - _lastSentAt < HeartbeatSeconds)
            {
                return;
            }

            _lastSendAttempt = now;

            if (!_session.SendView(
                    position.x, position.y, position.z,
                    rotation.x, rotation.y, rotation.z, rotation.w,
                    fov))
            {
                return;
            }

            _lastSentAt = now;
            _hasLastSent = true;
            _lastPosition = position;
            _lastRotation = rotation;
            _lastFov = fov;
        }

        void PruneStale(double now)
        {
            if (_peers.Count == 0)
            {
                return;
            }

            List<string> dead = null;
            foreach (var pair in _peers)
            {
                if (now - pair.Value.LastSeen <= StaleSeconds)
                {
                    continue;
                }

                if (dead == null)
                {
                    dead = new List<string>();
                }

                dead.Add(pair.Key);
            }

            if (dead == null)
            {
                return;
            }

            for (var i = 0; i < dead.Count; i++)
            {
                _peers.Remove(dead[i]);
            }

            RequestSceneViewRepaint();
        }

        void OnViewReceived(WireMessage message)
        {
            if (message == null || _session == null || !message.viewValid || !Show)
            {
                return;
            }

            // Never draw our own camera: we are looking through it.
            if (string.Equals(message.clientId, _session.ClientId, StringComparison.Ordinal))
            {
                return;
            }

            // A pose from another scene would point at objects that are not in this view.
            if (!string.IsNullOrEmpty(message.sceneGuid)
                && !string.IsNullOrEmpty(_session.SceneGuid)
                && !string.Equals(message.sceneGuid, _session.SceneGuid, StringComparison.Ordinal))
            {
                return;
            }

            PeerView peer;
            if (!_peers.TryGetValue(message.clientId ?? "", out peer))
            {
                peer = new PeerView
                {
                    ClientId = message.clientId ?? "",
                    Color = ParseColor(message.color),
                };
                _peers[peer.ClientId] = peer;
            }

            if (!string.IsNullOrEmpty(message.displayName))
            {
                peer.DisplayName = message.displayName;
            }

            peer.Position = new Vector3(message.viewPosX, message.viewPosY, message.viewPosZ);
            peer.Rotation = new Quaternion(message.viewRotX, message.viewRotY, message.viewRotZ, message.viewRotW);
            peer.Fov = message.viewFov > 1f ? message.viewFov : 60f;
            peer.LastSeen = EditorApplication.timeSinceStartup;

            RequestSceneViewRepaint();
        }

        static Color ParseColor(string hex)
        {
            Color color;
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out color))
            {
                return color;
            }

            return new Color(0.35f, 0.75f, 1f);
        }

        static void RequestSceneViewRepaint()
        {
            SceneView.RepaintAll();
        }

        void OnSceneGui(SceneView view)
        {
            if (!Show || _peers.Count == 0)
            {
                return;
            }

            foreach (var pair in _peers)
            {
                DrawCamera(view, pair.Value);
            }
        }

        static void DrawCamera(SceneView view, PeerView peer)
        {
            const float nearDistance = 0.15f;
            const float farDistance = 3f;

            var aspect = view != null && view.camera != null ? view.camera.aspect : 1.6f;
            var halfVertical = Mathf.Tan(peer.Fov * 0.5f * Mathf.Deg2Rad);

            var nearHalfH = halfVertical * nearDistance;
            var nearHalfW = nearHalfH * aspect;
            var farHalfH = halfVertical * farDistance;
            var farHalfW = farHalfH * aspect;

            var origin = peer.Position;
            var rotation = peer.Rotation;

            var near = new Corners(origin, rotation, nearDistance, nearHalfW, nearHalfH);
            var far = new Corners(origin, rotation, farDistance, farHalfW, farHalfH);

            var color = peer.Color;
            color.a = 0.85f;

            using (new Handles.DrawingScope(color))
            {
                // Body: a shallow pyramid from the eye to the near plane, so the pose reads as a camera.
                for (var i = 0; i < 4; i++)
                {
                    Handles.DrawLine(origin, near[i]);
                    Handles.DrawLine(near[i], far[i]);
                }

                Handles.DrawLine(near[0], near[1]);
                Handles.DrawLine(near[1], near[3]);
                Handles.DrawLine(near[3], near[2]);
                Handles.DrawLine(near[2], near[0]);
                Handles.DrawLine(far[0], far[1]);
                Handles.DrawLine(far[1], far[3]);
                Handles.DrawLine(far[3], far[2]);
                Handles.DrawLine(far[2], far[0]);

                // Up marker: turns a wire pyramid into something with an orientation.
                Handles.DrawLine(origin, origin + rotation * new Vector3(0f, nearHalfH * 2f, nearDistance));

                var label = string.IsNullOrEmpty(peer.DisplayName) ? "guest" : peer.DisplayName;
                Handles.Label(origin + rotation * new Vector3(0f, nearHalfH + 0.3f, nearDistance), label);
            }
        }

        struct Corners
        {
            readonly Vector3 _a, _b, _c, _d;

            public Corners(Vector3 origin, Quaternion rotation, float distance, float halfWidth, float halfHeight)
            {
                _a = origin + rotation * new Vector3(-halfWidth, -halfHeight, distance);
                _b = origin + rotation * new Vector3(halfWidth, -halfHeight, distance);
                _c = origin + rotation * new Vector3(-halfWidth, halfHeight, distance);
                _d = origin + rotation * new Vector3(halfWidth, halfHeight, distance);
            }

            public Vector3 this[int index]
            {
                get { return index == 0 ? _a : index == 1 ? _b : index == 2 ? _c : _d; }
            }
        }
    }
}
