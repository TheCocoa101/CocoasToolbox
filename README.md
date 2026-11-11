# Cocoa's Toolbox

VRChatアバター改変やUnity作業を効率化するエディタ拡張ツール集です。  
**VCC / ALCOM（VPM）** からリポジトリを追加してワンクリックで導入・バージョン更新できます。

> [!WARNING]
> **注意事項・免責事項**  
> 本ツールボックスに含まれる各機能は**バイブコーディング（AIコード生成）**によって作成された実験的・個人向けツールです。  
> コードの品質や完全な動作保証、将来的な互換性保証はございません。予期せぬ不具合やメッシュ・シーンの破損を防ぐため、**必ずプロジェクトやアセットのバックアップを取った上で自己責任にてご利用ください。**

---

## VCC / ALCOM への導入方法

1. 以下の公開ページを開き、**「✦ VCC / ALCOM にワンクリックで追加」** ボタンを押します（初回のみ）：
   - **公開ページ**: `https://thecocoa101.github.io/CocoasToolbox/`
   - **VPM リポジトリ URL（手動登録用）**: `https://thecocoa101.github.io/CocoasToolbox/index.json`
2. VCC / ALCOM の各プロジェクト管理画面で **Cocoa's Toolbox** の `+` ボタンを押してインストールします。
   - ※過去に `Assets/Cocoa's Toolbox` フォルダを直接配置していたプロジェクトでも、インストール時に `legacyFolders` 機能によって旧フォルダが自動整理され、スクリプト重複エラーなく移行できます。
   - 新バージョン公開時は、VCC / ALCOM の更新ボタン1つで全プロジェクトをアップデートできます。

---

## 収録ツール

Unity 上部メニューの **`Tools > Cocoa's Toolbox`** から開けます。

- **Armature Transfer** (`Tools > Cocoa's Toolbox > Armature Transfer`)
  - 素体側 Armature の Transform（Position / Rotation / Scale）、汎用コンポーネント、VRC PhysBone（Collider含む）、不足オブジェクトを衣装側 Armature の同名ボーンへ一括コピーします。
- **New Blendshape From Mix** (`Tools > Cocoa's Toolbox > New Blendshape From Mix`)
  - 複数の Blendshape を任意のウェイトでリアルタイムプレビューしながら合成し、元の FBX / Mesh を変更せず非破壊で新しい `.mesh` アセットとしてベイク保存します。
- **Missing Asset Importer** (`Tools > Cocoa's Toolbox > Missing Asset Importer`)
  - Migrate先のプロジェクトにて、元プロジェクトフォルダと Missing になっている Prefab をセットして `GET` ボタンを押すだけで、不足しているアセット（および連鎖する依存ファイル）だけを GUID を維持したまま `.unitypackage` 化してインポート画面を開きます。
- **Skinned To Static Mesh (Experimental)** (`Tools > Cocoa's Toolbox > Skinned To Static Mesh (Experimental)` / Hierarchy 右クリック `Cocoa's Toolbox`)
  - ボーン変形や BlendShape で曲げて形を作った状態の SkinnedMeshRenderer を、見た目上の位置・形状を維持したままボーンなしの StaticMesh（MeshFilter + MeshRenderer / `.mesh`）へ変換します。複製生成・その場変換、指定ボーンへの追従配置、複数メッシュの1メッシュ結合に対応。
- **Skinned Mesh Updater (Experimental)** (`Tools > Cocoa's Toolbox > Skinned Mesh Updater (Experimental)` / Hierarchy・SkinnedMeshRenderer 右クリック)
  - Blender等の外部ツールで編集・再エクスポートしたメッシュを既存の SkinnedMeshRenderer の Mesh 欄に置き換える際、座標系（軸回転）・スケール（×0.01 / ×100 等）・原点オフセット・ボーン順・バインドポーズ軸ズレによる表示破綻を Unity 側のみで自動補正します。シーン上でのリアルタイムプレビュー、新しい `.mesh` アセット作成、GUIDを維持した `.fbx` 上書き保存に対応。
- **Animation Path Adjuster (Experimental)** (`Tools > Cocoa's Toolbox > Animation Path Adjuster (Experimental)` / Project 右クリック `Cocoa's Toolbox`)
  - 複数の `.anim` ファイル（またはフォルダ・AnimatorController）をまとめて登録してパスを一括変更したり、1つの `.anim` 内の個別要素（シェイプキー・Active切替・マテリアル差替等）を複数選択してパスを書き換えられます。親階層の追加（`Body` → `○○/Body`）・削除・Hierarchyドロップ自動入力・基準アバターからの移動先パス自動探索に対応。
- **Hair Mesh Color Changer (Experimental)** (`Tools > Cocoa's Toolbox > Hair Mesh Color Changer (Experimental)` / Hierarchy 右クリック `Cocoa's Toolbox`)
  - 髪メッシュの一本一本（アイランド / 房）を SceneView 上でクリック選択（Blender の編集モード `L` キー風リンク選択）し、4つの基本色彩調整スライダー（色相・彩度・明度・コントラスト）で部分的に髪色を変更したり、lilToon 用のマスクテクスチャ（`.png`）を生成・自動設定できます。クリックと同時に選択箇所の UV がハイライトされるリアルタイム・テクスチャビュワー付き。
- **Hair Mesh Deleter (Experimental)** (`Tools > Cocoa's Toolbox > Hair Mesh Deleter (Experimental)` / Hierarchy 右クリック `Cocoa's Toolbox`)
  - 髪メッシュの一本一本を SceneView 上でクリック選択（`L` キー風リンク選択）し、リアルタイムに削除プレビュー確認しながら部分削除して、ボーンウェイト・BlendShape・全UVを維持した新しい `.mesh` アセットとして非破壊保存します。リアルタイム・テクスチャビュワー付き。
- **Blendshape Mesh Editor (Experimental)** (`Tools > Cocoa's Toolbox > Blendshape Mesh Editor (Experimental)` / Hierarchy・SkinnedMeshRenderer 右クリック)
  - SkinnedMeshRenderer に対して、SceneView 上で Blender の編集モードのように頂点・辺・面・アイランドを選択してプロポーショナル編集やギズモ・ブラシで変形し、その差分を新しい BlendShape（シェイプキー）として追加した `.mesh` アセットを新規保存します。ボーン変形・スケール・既存 BlendShape 下での逆スキニング計算、同座標頂点の自動結合移動（シーム割れ防止）、X軸ミラー対称編集に対応。
