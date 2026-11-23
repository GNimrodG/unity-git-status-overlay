using UnityEngine;

namespace GitStatusOverlayShowcase
{
    /// <summary>Tiny component used by the showcase scene (added to the Player's head as a prefab override).</summary>
    public class Spinner : MonoBehaviour
    {
        public float degreesPerSecond = 90f;

        private void Update()
        {
            transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f);
        }
    }
}
