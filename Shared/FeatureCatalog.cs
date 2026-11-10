#nullable disable
using System;
using System.Collections.Generic;

namespace PeakWorkshop.Protocol
{
    [Serializable]
    public sealed class FeatureValue
    {
        public string id = "";
        public bool enabled;
        public float value = 1f;
        public bool available;
        public string reason = "";
    }

    public sealed class FeatureDefinition
    {
        public readonly string Id, Name, Group, Description, Unit;
        public readonly bool HasValue;
        public readonly float Min, Max, Default, Step;
        public FeatureDefinition(string id, string name, string group, string description,
            bool hasValue = false, float min = 0, float max = 1, float initial = 1, float step = 0.1f, string unit = "×")
        { Id=id; Name=name; Group=group; Description=description; HasValue=hasValue; Min=min; Max=max; Default=initial; Step=step; Unit=unit; }
    }

    public static class FeatureCatalog
    {
        public static readonly string[] StatusNames = { "Injury", "Hunger", "Cold", "Poison", "Crab", "Curse", "Drowsy", "Weight", "Hot", "Thorns", "Spores", "Web", "Arrow", "Petrify", "FlyTrap" };
        private static readonly string[] StatusLabels = { "受伤", "饥饿", "寒冷", "中毒", "螃蟹", "诅咒", "困倦", "负重", "炎热", "荆棘", "孢子", "蛛网", "箭伤", "石化", "捕蝇草" };
        public static readonly FeatureDefinition[] All = Build();
        public static FeatureDefinition Find(string id)
        {
            foreach(var definition in All) if(definition.Id==id) return definition;
            return null;
        }
        private static FeatureDefinition[] Build()
        {
            var list=new List<FeatureDefinition>();
            list.Add(new FeatureDefinition("speed","移动速度倍率","移动与攀爬","调整移动速度；地形、碰撞和惯性仍会影响实际速度。",true,0.5f,200f));
            list.Add(new FeatureDefinition("climb.speed","攀爬速度倍率","移动与攀爬","调整爬墙、爬绳和爬藤时的移动速度。",true,0.2f,200f));
            list.Add(new FeatureDefinition("jump","跳跃高度倍率","移动与攀爬","数值越高，起跳越有力；实际跳高不会严格按同一倍数增加。",true,0.2f,200f));
            list.Add(new FeatureDefinition("fall.threshold","抗摔能力倍率","移动与攀爬","数值越高，越不容易因落地受伤；不能免除所有碰撞伤害。",true,0.1f,200f));
            list.Add(new FeatureDefinition("fall.disabled","防止摔倒","移动与攀爬","防止跌落引起的摔倒；不保证免除摔伤或死亡。"));
            list.Add(new FeatureDefinition("slippery.disabled","防滑","移动与攀爬","减少湿滑表面造成的下滑；体力耗尽时仍可能滑落。"));
            list.Add(new FeatureDefinition("stamina","无限体力","体力","让本地角色在需要消耗体力时保持无限体力。"));
            list.Add(new FeatureDefinition("stamina.consume","体力消耗倍率","体力","调整体力消耗量；0 为不消耗。与攀爬消耗同时开启时，两项倍率相乘。",true,0,5));
            list.Add(new FeatureDefinition("climb.consume","攀爬体力消耗倍率","体力","调整攀爬时的体力消耗量；0 为不消耗。",true,0,5));
            list.Add(new FeatureDefinition("stamina.regen","体力恢复倍率","体力","调整每次恢复或获得的体力量，包括物品带来的体力。",true,0,10));
            list.Add(new FeatureDefinition("stamina.delay","体力恢复等待时间","体力","停止消耗后等待指定秒数再恢复体力。",true,0,10,1,0.1f,"秒"));
            list.Add(new FeatureDefinition("status.all.block","不再增加负面状态","状态效果","阻止新的负面状态增加；已有状态请使用顶部的一键清除按钮。"));
            list.Add(new FeatureDefinition("status.all.scale","负面状态增加倍率","状态效果","调整所有负面状态的增加量；与单项倍率相乘，不影响已有状态。",true,0,5));
            list.Add(new FeatureDefinition("weight.disabled","无视物品负重","状态效果","暂时取消携带物品造成的负重；关闭后按当前物品恢复计算。"));
            for(int i=0;i<StatusNames.Length;i++)
            {
                list.Add(new FeatureDefinition("status."+StatusNames[i]+".block",StatusLabels[i]+"不再增加","单项状态","阻止该项负面状态继续增加；不会清除已经存在的状态。"));
                list.Add(new FeatureDefinition("status."+StatusNames[i]+".scale",StatusLabels[i]+"增加倍率","单项状态","调整该项状态的增加量；与全部状态倍率相乘，阻止增加开关优先。",true,0,5));
            }
            list.Add(new FeatureDefinition("fuel.unlimited","无限灯笼燃料","物品","手中灯笼不再消耗燃料；不会补回已经用掉的燃料。"));
            list.Add(new FeatureDefinition("fuel.consume","灯笼燃料消耗倍率","物品","调整手中灯笼的耗油量；0 为不消耗，不会补回已用燃料。",true,0,5));
            list.Add(new FeatureDefinition("items.unlimited","物品无限使用","物品","手中物品使用后不扣次数、不被消耗；不补回已经用掉的物品。"));
            list.Add(new FeatureDefinition("ping.size","标记手势大小倍率","物品","调整标记时手势的显示大小。",true,0.1f,50));
            list.Add(new FeatureDefinition("fog.stop","暂停迷雾推进","迷雾","让推进中的迷雾暂停；需要你是当前房主。"));
            list.Add(new FeatureDefinition("fog.speed","迷雾推进速度倍率","迷雾","调整迷雾后续推进速度；需要房主权限，关闭后不会倒退迷雾。",true,0,5));
            list.Add(new FeatureDefinition("fog.reveal","看穿迷雾","迷雾","提高你这边的迷雾可见度；关闭后恢复原来的显示。"));
            return list.ToArray();
        }
    }
}
