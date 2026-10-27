using System;
using UnityEngine;

//游戏中需要实现屏幕中循环的对象需挂载该脚本以注册循环效果
public class Game_ScreenLoopEffect : MonoBehaviour
{
        public void OnEnable()
        {
                Registry<Game_ScreenLoopEffect>.Add(this);
        }

        public void OnDisable()
        {
                Registry<Game_ScreenLoopEffect>.Remove(this);
        }
}