VPAI Creator

VPM/VCCパッケージをプロジェクトにインストールするunitypackage(VPAI unitypackage)を作るツールです。

インストール方法

a) インストーラを使用する場合
  1. VPAI-Creator-1.x.x-installer.unitypackageをunityプロジェクトにimportしてください。
  2. com.anatawa12.vpm-package-auto-installer.creatorをインストールするか聞かれるので`Install`を押してください。
  3. Packages/com.anatawa12.vpm-package-auto-installer.creatorが追加されます。

b) VCCに追加する場合
  1. VCCを最新版に更新してください。
  2. add-repo.urlファイルをを開いてください。VCCが起動します。
  3. VCCの指示に従いレポジトリを追加してください。
     もしレポジトリを追加済だとエラーになります。 (VCC 2.1.1時点)
  4. VCCからVPMPackageAutoInstaller Creatorを追加してください。

使い方
1. 上のツールバーからTools/VPMPackageAutoInstaller Creatorを選択してください。
2. パッケージの設定をします。
  2.1. もしpackage.jsonがすでにあるのであれば、それを`package.json`の欄に設定して"Load from package.json"を押してください
  2.2. package.jsonがなければ、example.pngを参考に手動で設定してください。
       Package Version Rangeの範囲のうち最新版がインストールされます。
       範囲の指定にはvpmDependenciesで使用するのと同じ、npmのバージョン範囲を使用できます。
       例えば `x.x.x`だと全てのバージョンの、`1.x.x` だとバージョン1.0.0(含む)から2.0.0(含まない)の、
       `1.2.x`だと1.2.0(含む)から1.3.0(含まない)のうち最新版をインストールします。
       また、`1.2.3`のようにすると`1.2.3`をインストールします。

同梱物
VPAI-Creator-1.x.x-installer.unitypackage
  VPAI Creatorのインストーラunitypackageです。
  これ自身も VPMPackageAutoInstaller を使用して作成しています。
  このunitypackageをプロジェクトにimportすると、VCCに
  vpm.anatawa12.com/vpm.jsonが追加され、unityプロジェクトに
  VPMPackageAutoInstaller Creatorが追加されます。

add-repo.url
  vpm.anatawa12.com/vpm.jsonをVCCに追加するためのリンクです。

README.ja.txt
  このファイルです。

LICENSE.txt
  ライセンス情報のファイルです。MIT Licenseです。
