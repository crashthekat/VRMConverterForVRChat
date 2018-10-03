using System.Linq;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using VRM;
using UniHumanoid;

namespace Esperecyan.Unity.VRMConverterForVRChat
{
    /// <summary>
    /// VRChatの不具合などに対処します。
    /// </summary>
    public class VRChatsBugsWorkaround
    {
        /// <summary>
        /// Cats Blender PluginでVRChat用に生成されるまばたきのシェイプキー名。
        /// </summary>
        /// <remarks>
        /// 参照:
        /// cats-blender-plugin/eyetracking.py at master · michaeldegroot/cats-blender-plugin
        /// <https://github.com/michaeldegroot/cats-blender-plugin/blob/master/tools/eyetracking.py>
        /// </remarks>
        private static readonly string[] OrderedBlinkGeneratedByCatsBlenderPlugin = {
            "vrc.blink_left",
            "vrc.blink_right",
            "vrc.lowerlid_left",
            "vrc.lowerlid_right"
        };

        /// <summary>
        /// クラスに含まれる処理を適用します。
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="assetsPath"></param>
        /// <param name="enableAutoEyeMovement">オートアイムーブメントを有効化するなら<c>true</c>、無効化するなら<c>false</c>。</param>
        internal static void Apply(GameObject avatar, string assetsPath, bool enableAutoEyeMovement)
        {
            VRChatsBugsWorkaround.AdjustHumanDescription(avatar: avatar, assetsPath: assetsPath);
            VRChatsBugsWorkaround.EnableAnimationOvrride(avatar: avatar, assetsPath: assetsPath);
            if (enableAutoEyeMovement)
            {
                VRChatsBugsWorkaround.EnableAutoEyeMovement(avatar: avatar, assetsPath: assetsPath);
            }
            else {
                VRChatsBugsWorkaround.DisableAutoEyeMovement(avatar: avatar, assetsPath: assetsPath);
            }
        }

        /// <summary>
        /// <see cref="HumanBodyBones.UpperChest"/>が存在する場合、それを<see cref="HumanBodyBones.Chest"/>とし、元の<see cref="HumanBodyBones.Chest"/>の関連付けは外すようにした。
        /// </summary>
        /// <seealso cref="VRC_SdkControlPanel.AnalyzeIK"/>
        /// <param name="avatar"></param>
        /// <param name="assetsPath"></param>
        private static void AdjustHumanDescription(GameObject avatar, string assetsPath)
        {
            var humanoidDescription = avatar.GetComponent<VRMHumanoidDescription>();
            bool isCreated;
            AvatarDescription avatarDescription = humanoidDescription.GetDescription(isCreated: out isCreated);

            List<BoneLimit> boneLimits = avatarDescription.human.ToList();
            var upperChest = boneLimits.FirstOrDefault(predicate: boneLimit => boneLimit.humanBone == HumanBodyBones.UpperChest);
            if (string.IsNullOrEmpty(upperChest.boneName)) {
                return;
            }

            avatarDescription = VRChatsBugsWorkaround.DuplicateObject(avatar: avatar, assetsPath: assetsPath, obj: avatarDescription) as AvatarDescription;

            boneLimits.Remove(boneLimits.First(predicate: boneLimit => boneLimit.humanBone == HumanBodyBones.Chest));

            upperChest.humanBone = HumanBodyBones.Chest;
            boneLimits[boneLimits.FindIndex(boneLimit => boneLimit.humanBone == HumanBodyBones.UpperChest)] = upperChest;

            avatarDescription.human = boneLimits.ToArray();
            ApplyAvatarDescription(avatar: avatar, assetsPath: assetsPath, avatarDescription: avatarDescription);
        }

        /// <summary>
        /// 指のボーンを補完し、アニメーションオーバーライドが機能するようにします。
        /// </summary>
        /// <remarks>
        /// 参照:
        /// 車軸制作所🌀mAtEyYEyLYE ouwua raudl/.さんのツイート: “Humanoidにしてるのになんで手の表情アニメーションオーバーライド動かないだーってなってたけど解決 ちゃんと指のボーンもHumanoidに対応づけないとダメなのね”
        /// <https://twitter.com/shajiku_works/status/977811702921150464>
        /// </remarks>
        /// <param name="avatar"></param>
        /// <param name="assetsPath"></param>
        private static void EnableAnimationOvrride(GameObject avatar, string assetsPath)
        {
            var humanoidDescription = avatar.GetComponent<VRMHumanoidDescription>();
            bool isCreated;
            AvatarDescription avatarDescription = humanoidDescription.GetDescription(isCreated: out isCreated);

            IEnumerable<HumanBodyBones> existedHumanBodyBones = avatarDescription.human.Select(boneLimit => boneLimit.humanBone);

            IEnumerable<BoneLimit> addedBoneLimits = VRChatUtility.RequiredHumanBodyBonesForAnimationOverride.Select(bones => {
                int missingHumanBodyBoneIndex = bones.ToList().FindIndex(match: bone => !existedHumanBodyBones.Contains(value: bone));
                if (missingHumanBodyBoneIndex == -1)
                {
                    return new BoneLimit[0];
                }
                
                Transform parent = avatar.GetComponent<Animator>().GetBoneTransform(humanBoneId: bones[missingHumanBodyBoneIndex - 1]);
                return bones.Skip(count: missingHumanBodyBoneIndex).Select(bone => {
                    Transform dummyBone = new GameObject(name: "vrc." + bone).transform;
                    dummyBone.parent = parent;
                    parent = dummyBone;
                    return new BoneLimit() { humanBone = bone, boneName = dummyBone.name };
                });
            }).ToList().SelectMany(boneLimit => boneLimit);

            if (addedBoneLimits.Count() == 0) {
                return;
            }

            BoneLimit[] boneLimits = avatarDescription.human.Concat(addedBoneLimits).ToArray();

            avatarDescription = VRChatsBugsWorkaround.DuplicateObject(avatar: avatar, assetsPath: assetsPath, obj: avatarDescription) as AvatarDescription;
            avatarDescription.human = boneLimits;
            ApplyAvatarDescription(avatar: avatar, assetsPath: assetsPath, avatarDescription: avatarDescription);
        }

        /// <summary>
        /// 変換前のアバターに関連するオブジェクトを複製して返します。
        /// </summary>
        /// <param name="avatar">複製したアバター。</param>
        /// <param name="assetsPath"></param>
        /// <returns>すでに複製されていた場合、そのまま返します。</returns>
        private static UnityEngine.Object DuplicateObject(GameObject avatar, string assetsPath, UnityEngine.Object obj)
        {
            var path = AssetDatabase.GetAssetPath(assetObject: obj);
            var newPath = Path.Combine(Converter.GetAnimationsFolderPath(avatar: avatar, assetsPath: assetsPath), obj.name + ".asset");
            if (path != newPath)
            {
                obj = GameObject.Instantiate(original: obj);
                AssetDatabase.CreateAsset(asset: obj, path: newPath);
            }
            EditorUtility.SetDirty(target: obj);
            return obj;
        }

        /// <summary>
        /// <see cref="Avatar"/>を作成して保存し、アバターに設定します。
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="assetsPath"></param>
        /// <param name="humanoidDescription"></param>
        /// <param name="avatarDescription"></param>
        private static void ApplyAvatarDescription(GameObject avatar, string assetsPath, AvatarDescription avatarDescription)
        {
            var humanoidDescription = avatar.GetComponent<VRMHumanoidDescription>();
            Avatar humanoidRig = AvatarBuilder.BuildHumanAvatar(
                go: avatar,
                humanDescription: avatarDescription.ToHumanDescription(root: avatar.transform)
            );
            AssetDatabase.CreateAsset(
                asset: humanoidRig,
                path: Path.Combine(Converter.GetAnimationsFolderPath(avatar: avatar, assetsPath: assetsPath), humanoidDescription.Avatar.name + ".asset")
            );
            humanoidDescription.Avatar = humanoidRig;
            avatar.GetComponent<Animator>().avatar = humanoidRig;
            EditorUtility.SetDirty(target: humanoidRig);
        }

        /// <summary>
        /// オートアイムーブメントが有効化される条件を揃えます。
        /// </summary>
        /// <remarks>
        /// 参照:
        /// 100の人さんのツイート: “Body当たりでした！　オートアイムーブメントの条件解明！ • ルート直下に、BlendShapeが4つ以上設定された「Body」という名前のオブジェクトが存在する • ルート直下に Armature/Hips/Spine/Chest/Neck/Head/RightEyeとLeftEye 　※すべて空のオブジェクトで良い 　※目のボーンの名称は何でも良い… https://t.co/dLnHl7QjJk”
        /// <https://twitter.com/esperecyan/status/1045713562348347392>
        /// </remarks>
        /// <param name="avatar"></param>
        /// <param name="assetsPath"></param>
        private static void EnableAutoEyeMovement(GameObject avatar, string assetsPath)
        {
            // ダミーの階層構造の作成
            foreach (var path in VRChatUtility.RequiredPathForAutoEyeMovement.Concat(new string[] { VRChatUtility.AutoBlinkMeshPath })) {
                var current = avatar.transform;
                foreach (var name in path.Split(separator: '/')) {
                    Transform child = current.Find(name: name);
                    if (!child) {
                        child = new GameObject(name: name).transform;
                        child.parent = current;
                    }
                    current = child;
                }
            }

            // ダミーのまばたき用ブレンドシェイプの作成
            var renderer = avatar.transform.Find(name: VRChatUtility.AutoBlinkMeshPath).gameObject.GetOrAddComponent<SkinnedMeshRenderer>();
            Mesh mesh = renderer.sharedMesh;
            if (mesh && mesh.blendShapeCount >= VRChatsBugsWorkaround.OrderedBlinkGeneratedByCatsBlenderPlugin.Length) {
                return;
            }

            var originalPath = mesh ? AssetDatabase.GetAssetPath(assetObject: mesh) : "dummy-for-auto-eye-movement.asset";
            var newPath = Path.Combine(Converter.GetAnimationsFolderPath(avatar: avatar, assetsPath: assetsPath), Path.GetFileName(path: originalPath));
            if (originalPath != newPath) {
                mesh = mesh ? GameObject.Instantiate<Mesh>(original: mesh) : VRChatsBugsWorkaround.CreateDummyMesh();
                AssetDatabase.CreateAsset(asset: mesh, path: newPath);
                renderer.sharedMesh = mesh;
            }
            
            foreach (var name in VRChatsBugsWorkaround.OrderedBlinkGeneratedByCatsBlenderPlugin.Skip(count: mesh.blendShapeCount)) {
                VRChatsBugsWorkaround.CreateDummyBlendShape(mesh: mesh, name: name);
            }
            
            EditorUtility.SetDirty(target: mesh);
        }

        /// <summary>
        /// オートアイムーブメントが有効化される条件が揃っていれば、破壊します。
        /// </summary>
        /// <param name="avatar"></param>
        private static void DisableAutoEyeMovement(GameObject avatar, string assetsPath)
        {
            var paths = VRChatUtility.RequiredPathForAutoEyeMovement.Concat(new string[] { VRChatUtility.AutoBlinkMeshPath });
            var transforms = paths.Concat(new string[] { VRChatUtility.AutoBlinkMeshPath }).Select(path => avatar.transform.Find(name: path));
            if (transforms.Contains(value: null))
            {
                return;
            }

            var renderer = avatar.transform.Find(name: VRChatUtility.AutoBlinkMeshPath).gameObject.GetOrAddComponent<SkinnedMeshRenderer>();
            Mesh mesh = renderer.sharedMesh;
            if (!mesh || mesh.blendShapeCount < VRChatsBugsWorkaround.OrderedBlinkGeneratedByCatsBlenderPlugin.Length)
            {
                return;
            }

            var eyeBones = new[] { HumanBodyBones.RightEye, HumanBodyBones.LeftEye }
                .Select(id => avatar.GetComponent<Animator>().GetBoneTransform(humanBoneId: id))
                .Where(bone => bone && transforms.Contains(value: bone));
            if (eyeBones.Count() == 0)
            {
                return;
            }

            var humanoidDescription = avatar.GetComponent<VRMHumanoidDescription>();
            bool isCreated;
            AvatarDescription avatarDescription = VRChatsBugsWorkaround.DuplicateObject(
                avatar: avatar,
                assetsPath: assetsPath,
                obj: humanoidDescription.GetDescription(isCreated: out isCreated)
            ) as AvatarDescription;

            var boneLimits = avatarDescription.human.ToList();
            foreach (Transform bone in eyeBones)
            {
                int index = boneLimits.FindIndex(match: limit => limit.boneName == bone.name);
                bone.name = bone.name.ToLower();
                BoneLimit boneLimit = boneLimits[index];
                boneLimit.boneName = bone.name;
                boneLimits[index] = boneLimit;
            }

            avatarDescription.human = boneLimits.ToArray();
            ApplyAvatarDescription(avatar: avatar, assetsPath: assetsPath, avatarDescription: avatarDescription);
        }

        /// <summary>
        /// ダミー用の空のメッシュを生成します。
        /// </summary>
        /// <returns></returns>
        private static Mesh CreateDummyMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(0, 0, 0) };
            return mesh;
        }

        /// <summary>
        /// ダミーのブレンドシェイプを作成します。
        /// </summary>
        /// <param name="mesh"></param>
        /// <param name="name"></param>
        private static void CreateDummyBlendShape(Mesh mesh, string name)
        {
            mesh.AddBlendShapeFrame(
                shapeName: name,
                frameWeight: 0,
                deltaVertices: new Vector3[mesh.vertexCount],
                deltaNormals: new Vector3[mesh.vertexCount],
                deltaTangents: new Vector3[mesh.vertexCount]
            );
        }
    }
}