using System;
using System.Collections.Generic;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace SceneCollab.Editor
{
    /// <summary>
    /// The list of scenes somebody is currently hosting, fetched from the server's <c>/rooms</c>.
    ///
    /// This is what makes "join the scene my colleague has open" possible without asking them for a
    /// room name over chat: the host's scene is the listing, and the guid in the listing is the thing
    /// the handshake checks. A client that cannot match the guid is refused, so a wrong pick is a clear
    /// error rather than a silent desync.
    ///
    /// Unity's <see cref="UnityWebRequest"/> is used rather than <c>HttpClient</c> because its completion
    /// is polled from the editor loop — no thread ever touches a Unity API.
    /// </summary>
    public sealed class CollabRoomList
    {
        [Serializable]
        public sealed class Room
        {
            public string room;
            public string sceneName;
            public string sceneGuid;
            public string host;
            public int participants;
            public int locks;
        }

        [Serializable]
        sealed class Payload
        {
            public Room[] rooms = new Room[0];
        }

        UnityWebRequest _request;
        Payload _payload;

        public IReadOnlyList<Room> Rooms
        {
            get { return _payload == null ? (IReadOnlyList<Room>)new Room[0] : _payload.rooms; }
        }

        public string Error { get; private set; }

        public bool Busy
        {
            get { return _request != null && !_request.isDone; }
        }

        /// <summary>Turns a websocket address into the HTTP base the REST endpoints live under.</summary>
        public static string HttpBaseFor(string serverUrl)
        {
            var normalized = CollabSettings.Normalize(serverUrl);
            if (normalized.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - 3);
            }

            if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + normalized.Substring("wss://".Length);
            }

            if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                return "http://" + normalized.Substring("ws://".Length);
            }

            return normalized;
        }

        public void Refresh(string serverUrl, string token)
        {
            Cancel();
            Error = null;

            var url = HttpBaseFor(serverUrl) + "/rooms";
            var request = UnityWebRequest.Get(url);
            if (!string.IsNullOrEmpty(token))
            {
                request.SetRequestHeader("X-SceneCollab-Token", token);
            }

            request.timeout = 8;
            _request = request;
            request.SendWebRequest();
        }

        /// <summary>Called from the window's update loop; harvests a finished request.</summary>
        public bool Poll()
        {
            if (_request == null || !_request.isDone)
            {
                return false;
            }

#if UNITY_2020_2_OR_NEWER
            var failed = _request.result != UnityWebRequest.Result.Success;
#else
            var failed = _request.isNetworkError || _request.isHttpError;
#endif

            if (failed)
            {
                Error = _request.responseCode == 401
                    ? "unauthorized — check the token"
                    : _request.error;
                _payload = null;
            }
            else
            {
                try
                {
                    _payload = JsonUtility.FromJson<Payload>(_request.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    Error = "unreadable room list: " + ex.Message;
                    _payload = null;
                }
            }

            _request.Dispose();
            _request = null;
            return true;
        }

        public void Cancel()
        {
            if (_request != null)
            {
                _request.Dispose();
                _request = null;
            }
        }

        /// <summary>True when the room edits the same scene this editor has open.</summary>
        public static bool MatchesActiveScene(Room room)
        {
            if (room == null || string.IsNullOrEmpty(room.sceneGuid))
            {
                return true;
            }

            var active = SceneIdentity.CaptureActive();
            if (!active.IsValid)
            {
                return false;
            }

            return string.Equals(active.guid, room.sceneGuid, StringComparison.Ordinal);
        }
    }
}
