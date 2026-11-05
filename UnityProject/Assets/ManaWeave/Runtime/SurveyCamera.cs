using UnityEngine;

namespace ManaWeave.Runtime
{
    /// <summary>Free-flight research camera, deliberately independent of character physics.</summary>
    public sealed class SurveyCamera : MonoBehaviour
    {
        private ManaWorldController _owner;
        private float _yaw, _pitch;
        private Vector3 _velocity;
        public bool Captured { get; private set; }

        public void Attach(ManaWorldController owner)
        {
            _owner = owner;
            ReadRotation();
        }

        public void Capture(bool capture)
        {
            Captured = capture;
            Cursor.lockState = capture ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !capture;
            _velocity = Vector3.zero;
        }

        public void Focus(Vector3 point, Vector3 offset)
        {
            transform.position = point + offset;
            transform.LookAt(point);
            ReadRotation();
            _velocity = Vector3.zero;
        }

        public void SetPose(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            ReadRotation();
            _velocity = Vector3.zero;
        }

        private void ReadRotation()
        {
            _yaw = transform.eulerAngles.y;
            _pitch = transform.eulerAngles.x;
            if (_pitch > 180) _pitch -= 360;
        }

        private void Update()
        {
            if (_owner == null || !Captured || _owner.WorkbenchOpen) return;
            _yaw += Input.GetAxisRaw("Mouse X") * 2.2f;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * 2.2f, -88, 88);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
            float horizontal = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            float forward = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            float vertical = (Input.GetKey(KeyCode.Space) ? 1 : 0) - ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C)) ? 1 : 0);
            Vector3 move = transform.right * horizontal + transform.forward * forward + Vector3.up * vertical;
            if (move.sqrMagnitude > 1) move.Normalize();
            float speed = Input.GetKey(KeyCode.LeftShift) ? 18 : 7;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _velocity = Vector3.Lerp(_velocity, move * speed, 1 - Mathf.Exp(-12 * dt));
            Vector3 p = transform.position + _velocity * dt;
            var world = _owner.World;
            // The camera can circle the finite test island, but cannot get lost beyond it.
            p.x = Mathf.Clamp(p.x, -20, world.Width + 20);
            p.y = Mathf.Clamp(p.y, 0.8f, world.Height + 30);
            p.z = Mathf.Clamp(p.z, -20, world.Depth + 20);
            transform.position = p;
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!focus) Capture(false);
        }

        private void OnDisable()
        {
            Capture(false);
        }
    }
}
