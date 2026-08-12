using Corehold.Data;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Corehold.UI
{
    /// <summary>
    /// Hover behaviour for a build-menu entry (GDD §9.1, §9.7). On pointer enter it
    /// asks the <see cref="BuildMenu"/> to preview the turret's range ring; on exit
    /// it clears it. Hover TINT for the buttons themselves is handled by Unity's
    /// Selectable colour tint (the pack's buttons have no hover sprite state, so
    /// tint is the documented workaround — GDD §9.7); this component only drives the
    /// range-ring preview.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildEntryHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private BuildMenu _menu;
        private TowerDefinition _def;

        public void Setup(BuildMenu menu, TowerDefinition def)
        {
            _menu = menu;
            _def = def;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_menu != null && _def != null)
                _menu.PreviewRange(_def);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_menu != null)
                _menu.ClearRangePreview();
        }
    }
}
