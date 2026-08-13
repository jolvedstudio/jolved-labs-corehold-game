using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace CoreholdEditor
{
    public static class PreviewBuildMenu
    {
        public static string Run()
        {
            var menu = SceneLookup.Find("Canvas_Menus/BuildMenu");
            if (menu == null) return "BuildMenu not found";
            menu.SetActive(true);

            var tmpl = menu.transform.Find("Entries/EntryTemplate");
            if (tmpl != null)
            {
                tmpl.gameObject.SetActive(true);
                var iconTr = tmpl.Find("Icon");
                var img = iconTr != null ? iconTr.GetComponent<Image>() : null;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_COREHOLD/Art/Icons/Tower_Autocannon.png");
                if (img != null && sprite != null) { img.sprite = sprite; img.enabled = true; }
            }
            Canvas.ForceUpdateCanvases();
            return "BuildMenu enabled for preview";
        }
    }
}
