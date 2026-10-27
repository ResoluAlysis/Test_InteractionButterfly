using UnityEngine;

//游戏中需要实现驱赶其他可被驱赶的对象需挂载该脚本以注册触发驱赶效果
public class Game_DriveSourceEffect : MonoBehaviour
{
    public void OnEnable() => Registry<Game_DriveSourceEffect>.Add(this);
    public void OnDisable() => Registry<Game_DriveSourceEffect>.Remove(this);
}