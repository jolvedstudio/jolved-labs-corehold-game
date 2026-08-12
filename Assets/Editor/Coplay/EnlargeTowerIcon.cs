using Coplay.Controllers.Functions;

public class EnlargeTowerIcon
{
    public static string Execute()
    {
        var r = CoplayTools.SetRectTransform(
            "Canvas_Menus/BuildMenu/Entries/EntryTemplate/Icon",
            anchorMin: "0.5,1",
            anchorMax: "0.5,1",
            sizeDelta: "58,58",
            anchoredPosition: "0,-6");
        return $"Enlarged tower icon: {r}";
    }
}
