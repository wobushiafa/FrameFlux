using UnityEngine;

namespace FrameFlux.Unity
{
    [RequireComponent(typeof(Renderer))]
    public sealed class MediaMaterial : MonoBehaviour
    {
        [SerializeField] private MediaView player;
        [SerializeField] private string textureProperty = "_MainTex";
        private Renderer target;
        private MaterialPropertyBlock properties;
        private void Awake() { target = GetComponent<Renderer>(); properties = new MaterialPropertyBlock(); }
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
            target.GetPropertyBlock(properties);
            properties.SetTexture(textureProperty, texture != null ? texture : Texture2D.blackTexture);
            target.SetPropertyBlock(properties);
        }
    }
}
