using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GaussianSplatting
{

// One auto-built row in the gallery list: a button bound to a fixed object index. The button's OnClick is
// configured to SendCustomEvent("Select") on this behaviour (Udon has no Button.onClick.AddListener), which
// routes the click back to the UI. This is internal UI plumbing, never wired by hand.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GalleryEntry : UdonSharpBehaviour
{
    public GaussianSplatRendererUI ui;
    public int index;
    public Button button;
    public Image background;
    public TextMeshProUGUI nameText;
    public TextMeshProUGUI countText;
    public TextMeshProUGUI descriptionText;

    public void Select()
    {
        if (ui != null)
        {
            ui.SelectGalleryIndex(index);
        }
    }
}

}
