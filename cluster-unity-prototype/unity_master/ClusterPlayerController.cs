using UnityEngine;

public class ClusterPlayerController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 8f;
    [SerializeField] private float hoverHeight = 0.9f;

    private Vector2 worldMin = new Vector2(-24f, -24f);
    private Vector2 worldMax = new Vector2(24f, 24f);
    private bool inputEnabled = true;

    public Vector2 Position2D
    {
        get
        {
            Vector3 position = transform.position;
            return new Vector2(position.x, position.z);
        }
    }

    public void Configure(float moveSpeed, Vector2 worldMin, Vector2 worldMax)
    {
        this.moveSpeed = moveSpeed;
        this.worldMin = worldMin;
        this.worldMax = worldMax;
    }

    public void ResetTo(Vector2 position)
    {
        transform.position = new Vector3(position.x, hoverHeight, position.y);
    }

    public void SetInputEnabled(bool enabled)
    {
        inputEnabled = enabled;
    }

    private void Update()
    {
        if (!inputEnabled)
        {
            return;
        }

        Vector2 input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        if (input.sqrMagnitude > 1f)
        {
            input.Normalize();
        }

        Vector2 nextPosition = Position2D + (input * moveSpeed * Time.deltaTime);
        nextPosition.x = Mathf.Clamp(nextPosition.x, worldMin.x, worldMax.x);
        nextPosition.y = Mathf.Clamp(nextPosition.y, worldMin.y, worldMax.y);
        transform.position = new Vector3(nextPosition.x, hoverHeight, nextPosition.y);

        if (input.sqrMagnitude > 0.0001f)
        {
            Vector3 forward = new Vector3(input.x, 0f, input.y);
            transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
    }
}
