using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // ULTRAKILL switches rooms off behind doors that close behind you (Door.Optimize), and on as their doors open. An
    // attempt recorded before the level's parts were (SectionRecorder) doesn't say which it had on, so a replay showing
    // it brings these rooms back instead, as copies that only draw (SectionPlayback), so it isn't running through
    // nothing: the ones Door.Optimize switched off this visit, and any other room a door switches that's off now.
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

        // The rooms off now (not a checkpoint's spare ones).
        public static List<GameObject> Rooms(LevelPaths level)
        {
            optimized.RemoveWhere(room => room == null);
            var rooms = new HashSet<GameObject>(optimized);
            foreach (Door door in Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (door != null && !level.InSpare(door.transform))
                {
                    Add(rooms, door.activatedRooms);
                    Add(rooms, door.deactivatedRooms);
                }
            }
            var off = new List<GameObject>();
            foreach (GameObject room in rooms)
            {
                if (room != null && !room.activeSelf && room.scene == level.Scene && !level.InSpare(room.transform))
                {
                    off.Add(room);
                }
            }
            return off;
        }

        private static void Add(HashSet<GameObject> rooms, GameObject[] more)
        {
            if (more == null)
            {
                return;
            }
            foreach (GameObject room in more)
            {
                if (room != null)
                {
                    rooms.Add(room);
                }
            }
        }
    }
}
