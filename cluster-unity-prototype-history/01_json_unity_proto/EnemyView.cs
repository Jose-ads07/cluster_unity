using UnityEngine;

public class EnemyView : MonoBehaviour
{
    [SerializeField] private float hoverHeight = 0.5f;
    [SerializeField] private Color seekerColor = new Color(0.93f, 0.25f, 0.25f);
    [SerializeField] private Color avoiderColor = new Color(0.22f, 0.82f, 0.95f);
    [SerializeField] private Vector3 seekerScale = new Vector3(0.9f, 0.9f, 0.9f);
    [SerializeField] private Vector3 avoiderScale = new Vector3(0.72f, 0.72f, 0.72f);

    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");

    private Renderer cachedRenderer;
    private MaterialPropertyBlock propertyBlock;

    public int EntityId { get; private set; }

    private void Awake()
    {
        cachedRenderer = GetComponentInChildren<Renderer>();
        propertyBlock = new MaterialPropertyBlock();

        Collider collider = GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
    }

    public void Initialize(int entityId)
    {
        EntityId = entityId;
        gameObject.name = string.Format("Enemy_{0}", entityId);
    }

    public void ConfigureAppearance(byte flags)
    {
        bool isAvoider = (flags & 0x1) != 0;
        transform.localScale = isAvoider ? avoiderScale : seekerScale;

        if (cachedRenderer == null)
        {
            return;
        }

        propertyBlock.Clear();
        Color tint = isAvoider ? avoiderColor : seekerColor;
        propertyBlock.SetColor(ColorPropertyId, tint);
        propertyBlock.SetColor(BaseColorPropertyId, tint);
        cachedRenderer.SetPropertyBlock(propertyBlock);
    }

    public void ApplyPosition(Vector2 position)
    {
        transform.position = new Vector3(position.x, hoverHeight, position.y);
    }

    public void ApplyVelocity(Vector2 velocity)
    {
        if (velocity.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        Vector3 forward = new Vector3(velocity.x, 0f, velocity.y);
        transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
    }
}
