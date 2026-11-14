using SceneCollab.Contracts.Protocol;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>SceneView-only lease visualization; no scene objects, meshes or materials are created.</summary>
    internal sealed class TerrainRegionOverlay
    {
        readonly TerrainSync _sync;
        readonly Vector3[] _quad = new Vector3[4];
        readonly Vector3[] _edge = new Vector3[17];
        readonly GUIContent _label = new GUIContent();
        GUIStyle _style;
        bool _bound;

        public TerrainRegionOverlay(TerrainSync sync) { _sync = sync; }

        public void Bind()
        {
            if (_bound) return;
            _bound = true;
            SceneView.duringSceneGui += Draw;
        }

        public void Unbind()
        {
            if (!_bound) return;
            _bound = false;
            SceneView.duringSceneGui -= Draw;
            SceneView.RepaintAll();
        }

        void Draw(SceneView view)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_style == null) _style = new GUIStyle(EditorStyles.helpBox) { richText = false, fontStyle = FontStyle.Bold };
            var oldColor = Handles.color;
            try
            {
                foreach (var terrain in _sync.VisibleTerrains)
                {
                    if (terrain == null || terrain.terrainData == null || !terrain.gameObject.activeInHierarchy) continue;
                    var guid = _sync.GuidFor(terrain.terrainData);
                    foreach (var lease in _sync.RegionLocks)
                    {
                        if (lease.terrainGuid != guid || lease.expiresAt <= _sync.ServerNow) continue;
                        var color = OwnerColor(lease.clientId);
                        var x0 = lease.x / (float)TerrainRegionRules.GridSize;
                        var z0 = lease.z / (float)TerrainRegionRules.GridSize;
                        var x1 = (lease.x + lease.width) / (float)TerrainRegionRules.GridSize;
                        var z1 = (lease.z + lease.height) / (float)TerrainRegionRules.GridSize;
                        _quad[0] = Point(terrain, x0, z0); _quad[1] = Point(terrain, x1, z0);
                        _quad[2] = Point(terrain, x1, z1); _quad[3] = Point(terrain, x0, z1);
                        Handles.DrawSolidRectangleWithOutline(_quad, new Color(color.r, color.g, color.b, 0.08f), color);
                        Handles.color = color;
                        // Subdivide the outline so occupied sectors follow sculpted terrain.
                        Edge(terrain, x0, z0, x1, z0); Edge(terrain, x1, z0, x1, z1);
                        Edge(terrain, x1, z1, x0, z1); Edge(terrain, x0, z1, x0, z0);
                        _label.text = (string.IsNullOrEmpty(lease.displayName) ? lease.clientId : lease.displayName)
                            + (lease.clientId == _sync.ClientId ? " (you)" : "");
                        _style.normal.textColor = color;
                        Handles.Label(Point(terrain, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f), _label, _style);
                    }
                }
            }
            finally { Handles.color = oldColor; }
        }

        void Edge(Terrain terrain, float x0, float z0, float x1, float z1)
        {
            for (var i = 0; i < _edge.Length; i++)
            {
                var t = i / (float)(_edge.Length - 1);
                _edge[i] = Point(terrain, Mathf.Lerp(x0, x1, t), Mathf.Lerp(z0, z1, t));
            }
            Handles.DrawAAPolyLine(2f, _edge);
        }

        static Vector3 Point(Terrain terrain, float x, float z)
        {
            var data = terrain.terrainData;
            return terrain.transform.position + new Vector3(x * data.size.x, data.GetInterpolatedHeight(x, z) + 0.15f, z * data.size.z);
        }

        static Color OwnerColor(string clientId)
        {
            uint hash = 2166136261;
            if (clientId != null) foreach (var c in clientId) unchecked { hash = (hash ^ c) * 16777619; }
            return Color.HSVToRGB((hash % 360) / 360f, 0.65f, 1f);
        }
    }
}
