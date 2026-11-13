using System.Collections.Generic;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // ULTRAKILL switches rooms off behind doors that close behind you (Door.Optimize), so a replay at
    // the end of a level would show earlier attempts running through nothing. These are the rooms it
    // switched off: a replay switches them back on (as walking back through the door would), and off
    // again after.
    internal static class RoomReveal
    {
        private static readonly HashSet<GameObject> optimized = new HashSet<GameObject>();

        public static void Track(GameObject[] rooms)
        {
            if (rooms == null)
            {
                return;
            }
            foreach (GameObject room in rooms)
            {
                if (room != null && room.activeSelf)
                {
                    optimized.Add(room);
                }
            }
        }

        public static List<GameObject> Reveal()
        {
            var revealed = new List<GameObject>();
            optimized.RemoveWhere(room => room == null);
            foreach (GameObject room in optimized)
            {
                if (!room.activeSelf)
                {
                    room.SetActive(true);
                    revealed.Add(room);
                }
            }
            return revealed;
        }

        public static void Hide(List<GameObject> revealed)
        {
            if (revealed == null)
            {
                return;
            }
            foreach (GameObject room in revealed)
            {
                if (room != null)
                {
                    room.SetActive(false);
                }
            }
        }
    }
}
