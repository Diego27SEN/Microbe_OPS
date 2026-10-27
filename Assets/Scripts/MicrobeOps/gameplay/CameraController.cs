using System.Collections;
using UnityEngine;

// Mueve la cámara de un tablero a otro con una transición suave.
// Ponlo en la Main Camera.
public class CameraController : MonoBehaviour
{
    [SerializeField] private float moveDuration = 0.6f; // 0 = salto instantáneo
    [SerializeField] private AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    private Coroutine moving;

    public bool IsMoving => moving != null;

    public void MoveTo(Vector3 worldPos, bool instant = false)
    {
        var target = new Vector3(worldPos.x, worldPos.y, transform.position.z); // conserva la Z

        if (moving != null) StopCoroutine(moving);

        if (instant || moveDuration <= 0f)
        {
            transform.position = target;
            moving = null;
            return;
        }

        moving = StartCoroutine(MoveRoutine(target));
    }

    private IEnumerator MoveRoutine(Vector3 target)
    {
        Vector3 start = transform.position;
        float t = 0f;

        while (t < moveDuration)
        {
            t += Time.deltaTime;
            float k = ease.Evaluate(Mathf.Clamp01(t / moveDuration));
            transform.position = Vector3.LerpUnclamped(start, target, k);
            yield return null;
        }

        transform.position = target;
        moving = null;
    }
}