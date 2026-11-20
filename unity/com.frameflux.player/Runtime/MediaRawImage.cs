using UnityEngine;
using UnityEngine.UI;

namespace FrameFlux.Unity
{
    [RequireComponent(typeof(RawImage))]
    public sealed class MediaRawImage : MonoBehaviour
    {
        [SerializeField] private MediaView player;
        [SerializeField] private bool preserveAspect = true;
        private RawImage image;
        private AspectRatioFitter aspect;
        private void Awake() { image = GetComponent<RawImage>(); aspect = GetComponent<AspectRatioFitter>(); }
        private void OnEnable()
        {
            if (player == null) player = GetComponent<MediaView>();
            if (player == null) return;
            player.TextureChanged += OnTexture;
            OnTexture(player.Texture);
        }
        private void OnDisable() { if (player != null) player.TextureChanged -= OnTexture; }
        private void OnTexture(Texture2D texture)
        {
            image.texture = texture;
            image.uvRect = new Rect(0, 0, 1, 1);
            if (preserveAspect && aspect != null && texture != null) aspect.aspectRatio = (float)texture.width / texture.height;
        }
    }
}
