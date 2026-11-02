using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor
{
    /// <summary>
    /// Per-project connection defaults: where the server is, which room, what name to show.
    ///
    /// The access token deliberately does NOT live here. This asset is written into
    /// <c>ProjectSettings/</c> and committed by most teams, and a committed token is a leaked
    /// token. It goes to <see cref="EditorPrefs"/> instead — per machine, per user, out of Git.
    /// </summary>
    [Serializable]
    public sealed class CollabSettings
    {
        const string AssetPath = "ProjectSettings/SceneCollabSettings.json";
        const string TokenKey = "SceneCollab.Token";
        const string DisplayNameKey = "SceneCollab.DisplayName";

        public string serverUrl = "ws://127.0.0.1:8080/ws";
        public string room = "default";

        /// <summary>
        /// Name other participants see. Kept per user, because a shared name in a committed
        /// file means everyone arrives as the same person.
        /// </summary>
        public string displayName = "";

        /// <summary>
        /// Optional prefab used as the body of an ERROR marker, as a project-relative asset path (JSON cannot
        /// hold object references). Empty means the built-in painted ERROR sign. Point this at whatever your project
        /// considers an obviously-missing placeholder — the package itself ships no third-party art.
        /// </summary>
        public string errorModelPath = "";

        /// <summary>Access token, stored outside the project. Never serialized to the asset.</summary>
        public static string Token
        {
            get { return EditorPrefs.GetString(TokenKey, ""); }
            set { EditorPrefs.SetString(TokenKey, value ?? ""); }
        }

        public static bool HasToken
        {
            get { return !string.IsNullOrEmpty(Token); }
        }

        public static CollabSettings Load()
        {
            var settings = new CollabSettings();

            if (File.Exists(AssetPath))
            {
                try
                {
                    var json = File.ReadAllText(AssetPath);
                    var loaded = JsonUtility.FromJson<CollabSettings>(json);
                    if (loaded != null)
                    {
                        settings = loaded;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SceneCollab] could not read " + AssetPath + ": " + ex.Message);
                }
            }

            settings.displayName = EditorPrefs.GetString(DisplayNameKey, settings.displayName);
            settings.serverUrl = Normalize(settings.serverUrl);
            return settings;
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(AssetPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(AssetPath, JsonUtility.ToJson(this, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SceneCollab] could not write " + AssetPath + ": " + ex.Message);
            }

            EditorPrefs.SetString(DisplayNameKey, displayName ?? "");
        }

        /// <summary>
        /// Normalises user input into a URL the client accepts: adds ws:// when the scheme is
        /// missing and the /ws path when only a host was typed. People paste host:port.
        /// </summary>
        public static string Normalize(string url)
        {
            var value = (url ?? "").Trim();
            if (value.Length == 0)
            {
                return "ws://127.0.0.1:8080/ws";
            }

            if (!value.Contains("://"))
            {
                value = "ws://" + value;
            }

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                return value;
            }

            if (uri.Scheme == "http")
            {
                value = "ws" + value.Substring(4);
            }
            else if (uri.Scheme == "https")
            {
                value = "wss" + value.Substring(5);
            }

            var path = uri.AbsolutePath;
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                value = value.TrimEnd('/') + "/ws";
            }

            return value;
        }

        /// <summary>Adds the room to the query string, which is how the server keys rooms.</summary>
        public string BuildConnectionUrl()
        {
            var url = Normalize(serverUrl);
            var separator = url.Contains("?") ? "&" : "?";
            return url + separator + "room=" + Uri.EscapeDataString(string.IsNullOrEmpty(room) ? "default" : room);
        }
    }
}
