using UnityEngine;

namespace Corehold.UI
{
    /// <summary>
    /// A flat ground ring that previews a turret's range on a hardpoint (GDD §9.1).
    /// A single <see cref="LineRenderer"/> circle laid on the ground plane, using
    /// one shared additive-ish material. Shown by <see cref="BuildMenu"/> while a
    /// build entry is hovered / selected, hidden otherwise.
    /// </summary>
    [DisallowMultipleComponent]
    public class RangeRing : MonoBehaviour
    {
        [SerializeField] private int segments = 48;
        [SerializeField] private float lineWidth = 0.25f;
        [SerializeField] private float groundOffset = 0.1f;
        [SerializeField] private Color color = new Color(0.2f, 0.85f, 1f, 0.7f);

        private LineRenderer _line;

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            if (_line == null)
                _line = gameObject.AddComponent<LineRenderer>();

            _line.useWorldSpace = true;
            _line.loop = true;
            _line.widthMultiplier = lineWidth;
            _line.positionCount = segments;
            _line.numCornerVertices = 2;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;

            if (_line.sharedMaterial == null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                var mat = new Material(sh) { name = "RangeRingMat" };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                mat.color = color;
                _line.sharedMaterial = mat;
            }
            _line.startColor = color;
            _line.endColor = color;

            Hide();
        }

        public void Show(Vector3 center, float radius)
        {
            if (_line == null) return;
            _line.enabled = true;
            for (int i = 0; i < segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Cos(a) * radius, groundOffset, Mathf.Sin(a) * radius);
                _line.SetPosition(i, p);
            }
        }

        public void Hide()
        {
            if (_line != null)
                _line.enabled = false;
        }
    }
}
