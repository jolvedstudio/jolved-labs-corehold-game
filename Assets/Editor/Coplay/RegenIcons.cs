using Corehold.EditorTools;

namespace CoreholdEditor
{
    public static class RegenIcons
    {
        public static string Execute()
        {
            IconRenderer.RenderIcons();
            return "Regenerated icons.";
        }
    }
}
