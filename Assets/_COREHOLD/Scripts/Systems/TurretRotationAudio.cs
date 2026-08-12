using System.Collections.Generic;
using Corehold.Towers;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Drives the turret rotation-loop SFX policy (GDD §10).
    ///
    /// The rotation loop must play <b>only while a turret is actively slewing</b>
    /// and <b>only for the three turrets nearest screen centre</b> — ten turrets
    /// slewing at once is a wall of noise. This component owns that policy so the
    /// <see cref="AudioDirector"/> stays a pure playback service: each frame it
    /// walks <see cref="Tower.Live"/>, keeps the turrets that are slewing this
    /// frame (via <see cref="TurretAim.IsSlewing"/>), sorts them by squared screen
    /// distance to centre, and tells the director how many of its dedicated
    /// rotation-loop voices to sound with <see cref="AudioDirector.SetRotationLoud"/>.
    ///
    /// It allocates nothing per frame — the working list is reused (GDD §11).
    /// </summary>
    [DisallowMultipleComponent]
    public class TurretRotationAudio : MonoBehaviour
    {
        [Tooltip("Camera used to measure screen-centre distance. Falls back to Camera.main.")]
        [SerializeField] private Camera viewCamera;

        // Reused each frame so the policy allocates nothing (GDD §11).
        private readonly List<TurretAim> _slewing = new List<TurretAim>(16);

        private void Update()
        {
            AudioDirector audio = AudioDirector.Instance;
            if (audio == null)
                return;

            int maxVoices = audio.RotationLoopVoices;
            if (maxVoices <= 0)
                return;

            Camera cam = viewCamera != null ? viewCamera : Camera.main;

            _slewing.Clear();
            var towers = Tower.Live;
            for (int i = 0; i < towers.Count; i++)
            {
                Tower t = towers[i];
                if (t == null)
                    continue;

                // A support relay has no barrel to slew; skip it.
                if (t.IsSupportRelay)
                    continue;

                TurretAim aim = t.GetComponent<TurretAim>();
                if (aim != null && aim.IsSlewing)
                    _slewing.Add(aim);
            }

            // Fewer slewing than we can voice: everyone that is slewing is loud.
            int loud = _slewing.Count;

            if (loud > maxVoices && cam != null)
            {
                // More slewing turrets than voices: keep the three (maxVoices)
                // nearest screen centre (GDD §10). Partial selection by squared
                // screen-space distance to the viewport centre (0.5, 0.5).
                _slewing.Sort((a, b) => ScreenDistSqr(cam, a).CompareTo(ScreenDistSqr(cam, b)));
                loud = maxVoices;
            }
            else if (loud > maxVoices)
            {
                // No camera to rank by — just cap the count.
                loud = maxVoices;
            }

            audio.SetRotationLoud(loud);
        }

        private void OnDisable()
        {
            // Silence the loop if this policy is turned off mid-slew.
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.SetRotationLoud(0);
        }

        /// <summary>Squared distance of a turret from the viewport centre in screen space.</summary>
        private static float ScreenDistSqr(Camera cam, TurretAim aim)
        {
            Vector3 vp = cam.WorldToViewportPoint(aim.transform.position);
            // Behind the camera → push to the back so it is never chosen.
            if (vp.z < 0f)
                return float.MaxValue;
            float dx = vp.x - 0.5f;
            float dy = vp.y - 0.5f;
            return dx * dx + dy * dy;
        }
    }
}
