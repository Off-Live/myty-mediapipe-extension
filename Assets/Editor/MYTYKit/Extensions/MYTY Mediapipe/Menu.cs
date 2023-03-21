using MYTYKit.MotionTemplates;
using UnityEditor;
using UnityEngine;


namespace MYTYKit.Extensions.Mediapipe
{
    public class Menu 
    {
        [MenuItem("MYTY Kit/Extensions/Create Mediapipe Motion Source", false, 100)]
        static void CreateMotionSource()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/MYTYKit/Extensions/MYTY Mediapipe/Prefabs/MediapipeMotionPack.prefab");

            var go = Object.Instantiate(asset);
            go.name = "MediapipeMotionPack";

            var motionTemplate = GameObject.FindObjectOfType<MotionTemplateMapper>();
            if (motionTemplate != null)
            {
                var motionSource = go.GetComponentInChildren<MotionSource>();
                motionSource.motionTemplateMapperList = new();
                motionSource.motionTemplateMapperList.Add(motionTemplate);
            }
            
        }
    }
}
