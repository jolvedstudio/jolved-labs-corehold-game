using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// Carousel behaviour for the build menu (10-slot roster): the entries row
    /// lives in a clipped ScrollRect showing six cells; edge arrows page it and
    /// drag/wheel scrolling works natively. Arrows hide themselves at the ends
    /// (and entirely when everything fits). Built and wired by BuildRealUI.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildMenuCarousel : MonoBehaviour
    {
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private Button leftButton;
        [SerializeField] private Button rightButton;

        [Tooltip("[TUNE] Cells advanced per arrow tap.")]
        [SerializeField] private int cellsPerTap = 2;

        [Tooltip("[TUNE] One cell's width + spacing, in reference pixels.")]
        [SerializeField] private float cellStep = 148f;

        private void OnEnable()
        {
            if (leftButton != null) leftButton.onClick.AddListener(PageLeft);
            if (rightButton != null) rightButton.onClick.AddListener(PageRight);
        }

        private void OnDisable()
        {
            if (leftButton != null) leftButton.onClick.RemoveListener(PageLeft);
            if (rightButton != null) rightButton.onClick.RemoveListener(PageRight);
        }

        private void PageLeft() => Page(-1);
        private void PageRight() => Page(1);

        private void Page(int dir)
        {
            if (scrollRect == null || scrollRect.content == null)
                return;
            var content = scrollRect.content;
            Vector2 pos = content.anchoredPosition;
            pos.x = Mathf.Clamp(pos.x - dir * cellsPerTap * cellStep, MinX(), 0f);
            content.anchoredPosition = pos;
        }

        private float MinX()
        {
            if (scrollRect == null || scrollRect.content == null || scrollRect.viewport == null)
                return 0f;
            return Mathf.Min(0f, scrollRect.viewport.rect.width - scrollRect.content.rect.width);
        }

        private void Update()
        {
            if (scrollRect == null || scrollRect.content == null)
                return;

            float min = MinX();
            bool scrollable = min < -1f;
            float x = scrollRect.content.anchoredPosition.x;

            if (leftButton != null)
                leftButton.gameObject.SetActive(scrollable && x < -1f);
            if (rightButton != null)
                rightButton.gameObject.SetActive(scrollable && x > min + 1f);
        }
    }
}
