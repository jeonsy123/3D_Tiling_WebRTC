using UnityEngine;

public class MouseCameraController : MonoBehaviour
{
    public float rotationSpeed = 5.0f;

    private float yaw = 0.0f;
    private float pitch = 0.0f;

    void Update()
    {
        if (Input.GetMouseButton(0))  // 왼쪽 마우스 버튼
        {
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");

            yaw += mouseX * rotationSpeed;
            pitch -= mouseY * rotationSpeed;
            pitch = Mathf.Clamp(pitch, -80f, 80f);  // 위아래 각도 제한

            transform.eulerAngles = new Vector3(pitch, yaw, 0.0f);
        }
    }
}
