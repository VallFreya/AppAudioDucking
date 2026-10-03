using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SmoothMuterPlugin
{
    /// <summary>
    /// Наш собственный класс для вызова C-функций из главного файла obs.dll
    /// </summary>
    public static unsafe class ObsApi
    {
        // Указываем имя системного модуля OBS
        private const string OBS_DLL = "obs";

        // Функция создания контейнера свойств (окон настроек)
        [DllImport(OBS_DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr obs_properties_create();

        // Функция добавления текстового поля
        [DllImport(OBS_DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr obs_properties_add_text(IntPtr props, char* name, char* description, int type);

        // Функция добавления выпадающего списка (Dropdown)
        [DllImport(OBS_DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr obs_properties_add_list(IntPtr props, char* name, char* description, int comboType, int comboFormat);

        // Функция добавления элемента в выпадающий список
        [DllImport(OBS_DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr obs_property_list_add_string(IntPtr p, char* name, char* val);

        // Функция чтения строковых значений из сохраненных настроек OBS
        [DllImport(OBS_DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr obs_data_get_string(IntPtr data, char* name);
    }
}
