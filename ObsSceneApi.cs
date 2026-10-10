using System.Runtime.InteropServices;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Список сцен OBS и звуковых источников сцены (для выпадающих списков в настройках)
    /// </summary>
    internal static class ObsSceneApi
    {
        private const uint ObsSourceAudio = 1u << 1; // OBS_SOURCE_AUDIO
        private const uint LastGlobalChannel = 6;    // каналы 1..6 хранят глобальные аудиоисточники

        // struct obs_frontend_source_list { DARRAY(obs_source_t *) sources; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObsSourceList
        {
            public IntPtr Array;
            public nuint Num;
            public nuint Capacity;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool SceneItemProc(IntPtr scene, IntPtr item, IntPtr param);

        [DllImport("obs-frontend-api", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_frontend_get_scenes(ref ObsSourceList list);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_source_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_output_source(uint channel);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_source_get_name(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_source_get_id(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint obs_source_get_output_flags(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_source_release(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_scene_from_source(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_scene_enum_items(IntPtr scene, SceneItemProc callback, IntPtr param);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_sceneitem_get_source(IntPtr item);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool obs_sceneitem_is_group(IntPtr item);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_sceneitem_group_enum_items(IntPtr group, SceneItemProc callback, IntPtr param);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void bfree(IntPtr ptr);

        // Состояние текущего обхода. Обход идёт синхронно, поэтому достаточно одной блокировки
        private static readonly object EnumLock = new object();
        private static readonly SceneItemProc ItemProc = OnSceneItem;
        private static List<string> _found = new List<string>();
        private static HashSet<string> _foundSet = new HashSet<string>();
        private static HashSet<IntPtr> _visitedScenes = new HashSet<IntPtr>();

        /// <summary>Имена сцен в том порядке, в котором они показаны в OBS</summary>
        public static List<string> GetSceneNames()
        {
            var result = new List<string>();
            var list = new ObsSourceList();

            obs_frontend_get_scenes(ref list);
            try
            {
                for (int i = 0; i < (int)list.Num; i++)
                {
                    IntPtr source = Marshal.ReadIntPtr(list.Array, i * IntPtr.Size);
                    string? name = source != IntPtr.Zero ? Marshal.PtrToStringUTF8(obs_source_get_name(source)) : null;
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
            }
            finally
            {
                // то же, что делает inline-функция obs_frontend_source_list_free
                for (int i = 0; i < (int)list.Num; i++)
                {
                    IntPtr source = Marshal.ReadIntPtr(list.Array, i * IntPtr.Size);
                    if (source != IntPtr.Zero) obs_source_release(source);
                }
                if (list.Array != IntPtr.Zero) bfree(list.Array);
            }

            return result;
        }

        /// <summary>
        /// Звуковые источники сцены, включая вложенные сцены и группы.
        /// Для пустого имени сцены возвращает глобальные источники (микрофон, desktop audio).
        /// </summary>
        public static List<string> GetAudioSourceNames(string sceneName)
        {
            lock (EnumLock)
            {
                _found = new List<string>();
                _foundSet = new HashSet<string>();
                _visitedScenes = new HashSet<IntPtr>();

                try
                {
                    if (string.IsNullOrEmpty(sceneName))
                    {
                        CollectGlobalSources();
                    }
                    else
                    {
                        IntPtr sceneSource = obs_get_source_by_name(sceneName);
                        if (sceneSource != IntPtr.Zero)
                        {
                            try
                            {
                                // сцена принадлежит источнику, отдельной ссылки на неё нет
                                CollectScene(obs_scene_from_source(sceneSource));
                            }
                            finally
                            {
                                obs_source_release(sceneSource);
                            }
                        }
                    }

                    return _found;
                }
                finally
                {
                    _foundSet = new HashSet<string>();
                    _visitedScenes = new HashSet<IntPtr>();
                }
            }
        }

        private static void CollectGlobalSources()
        {
            for (uint channel = 1; channel <= LastGlobalChannel; channel++)
            {
                IntPtr source = obs_get_output_source(channel);
                if (source == IntPtr.Zero) continue;

                try
                {
                    AddIfAudio(source);
                }
                finally
                {
                    obs_source_release(source);
                }
            }
        }

        private static void CollectScene(IntPtr scene)
        {
            if (scene == IntPtr.Zero || !_visitedScenes.Add(scene)) return;
            obs_scene_enum_items(scene, ItemProc, IntPtr.Zero);
        }

        // Вызывается из libobs во время обхода, исключения наружу выпускать нельзя
        private static bool OnSceneItem(IntPtr scene, IntPtr item, IntPtr param)
        {
            try
            {
                if (obs_sceneitem_is_group(item))
                {
                    obs_sceneitem_group_enum_items(item, ItemProc, IntPtr.Zero);
                    return true;
                }

                // ссылка на источник элемента не выдаётся, освобождать его не нужно
                IntPtr source = obs_sceneitem_get_source(item);
                if (source == IntPtr.Zero) return true;

                if (Marshal.PtrToStringUTF8(obs_source_get_id(source)) == "scene")
                {
                    CollectScene(obs_scene_from_source(source));
                    return true;
                }

                AddIfAudio(source);
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Error enumerating scene items: {ex.Message}");
            }

            return true;
        }

        private static void AddIfAudio(IntPtr source)
        {
            if ((obs_source_get_output_flags(source) & ObsSourceAudio) == 0) return;

            string? name = Marshal.PtrToStringUTF8(obs_source_get_name(source));
            if (!string.IsNullOrEmpty(name) && _foundSet.Add(name))
            {
                _found.Add(name);
            }
        }
    }
}
