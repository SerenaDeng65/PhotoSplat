using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

namespace PhotoSplat
{
    /// <summary>
    /// Thumb-index pinch from XR Hands (either hand; the hand that pinched first owns the pinch).
    /// Without hand tracking (Editor, no headset) the left mouse button pinches at a point under the
    /// mouse, <see cref="editorDistance"/> in front of the camera (scroll wheel changes it).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PinchInput : MonoBehaviour
    {
        [Tooltip("Fingertips closer than this (m) start a pinch.")] public float pinchOn = 0.02f;
        [Tooltip("Fingertips farther than this (m) end it.")] public float pinchOff = 0.035f;
        [Tooltip("Editor fallback: how far in front of the camera the mouse 'hand' is (m).")] public float editorDistance = 0.4f;
        [Tooltip("In the Editor, ignore hand tracking (incl. the XR Interaction Simulator's fake hands) and use mouse / Space.")]
        public bool mouseInEditor = true;

        public bool IsPinching { get; private set; }
        public bool PinchStarted { get; private set; }
        public bool PinchEnded { get; private set; }
        public Vector3 PinchPoint { get; private set; }
        /// <summary>Both hands pinching at once (used for reset).</summary>
        public bool BothPinching { get; private set; }
        public bool UsingHands { get; private set; }

        static readonly List<XRHandSubsystem> s_Subsystems = new List<XRHandSubsystem>();
        XRHandSubsystem m_Hands;
        readonly bool[] m_HandPinch = new bool[2];
        readonly Vector3[] m_HandPoint = new Vector3[2];
        int m_Owner = -1;

        void Update()
        {
            bool was = IsPinching;
            var cam = Camera.main;

            if (m_Hands == null || !m_Hands.running)
            {
                m_Hands = null;
                SubsystemManager.GetSubsystems(s_Subsystems);
                foreach (var s in s_Subsystems) if (s.running) { m_Hands = s; break; }
            }

            bool anyTracked = false;
            bool useHands = !(Application.isEditor && mouseInEditor);
            if (useHands && m_Hands != null && cam != null)
            {
                // hand joint poses are in the same tracking space as the head (the camera's local pose)
                Transform space = cam.transform.parent;
                for (int h = 0; h < 2; h++)
                {
                    XRHand hand = h == 0 ? m_Hands.leftHand : m_Hands.rightHand;
                    if (!hand.isTracked ||
                        !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumb) ||
                        !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose index))
                    {
                        m_HandPinch[h] = false;
                        continue;
                    }
                    anyTracked = true;
                    float d = Vector3.Distance(thumb.position, index.position);
                    m_HandPinch[h] = m_HandPinch[h] ? d < pinchOff : d < pinchOn;
                    Vector3 mid = (thumb.position + index.position) * 0.5f;
                    m_HandPoint[h] = space != null ? space.TransformPoint(mid) : mid;
                }
            }

            UsingHands = anyTracked;
            if (anyTracked)
            {
                if (m_Owner >= 0 && !m_HandPinch[m_Owner]) m_Owner = -1;
                if (m_Owner < 0) m_Owner = m_HandPinch[1] ? 1 : m_HandPinch[0] ? 0 : -1;
                IsPinching = m_Owner >= 0;
                if (IsPinching) PinchPoint = m_HandPoint[m_Owner];
                BothPinching = m_HandPinch[0] && m_HandPinch[1];
            }
            else
            {
                m_HandPinch[0] = m_HandPinch[1] = false;
                m_Owner = -1;
                BothPinching = false;
                var mouse = Mouse.current;
                // left mouse button / trackpad click, or hold Space
                IsPinching = (mouse != null && mouse.leftButton.isPressed)
                          || (Keyboard.current != null && Keyboard.current.spaceKey.isPressed);
                if (mouse != null && cam != null)
                {
                    editorDistance = Mathf.Clamp(editorDistance + mouse.scroll.ReadValue().y * 0.0005f, 0.15f, 3f);
                    Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                    PinchPoint = ray.GetPoint(editorDistance);
                }
            }

            PinchStarted = IsPinching && !was;
            PinchEnded = !IsPinching && was;
        }
    }
}
