# GitHub 上传项目教程

本文介绍如何把本地项目上传到 GitHub。适用于 Windows、macOS 和 Linux，命令行示例以 Git 为主。

## 1. 准备工作

### 1.1 注册 GitHub 账号

打开 [GitHub 官网](https://github.com/)，注册并登录账号。

### 1.2 安装 Git

下载并安装 Git：

[https://git-scm.com/downloads](https://git-scm.com/downloads)

安装完成后，打开终端或 PowerShell，运行：

```bash
git --version
```

如果能看到版本号，说明安装成功。

### 1.3 配置 Git 用户信息

首次使用 Git 时，需要配置用户名和邮箱：

```bash
git config --global user.name "你的用户名"
git config --global user.email "你的邮箱@example.com"
```

查看配置：

```bash
git config --global --list
```

## 2. 在 GitHub 创建远程仓库

1. 登录 GitHub。
2. 点击右上角 `+`，选择 `New repository`。
3. 填写仓库名称，例如 `my-project`。
4. 选择仓库可见性：
   - `Public`：公开仓库，所有人可见。
   - `Private`：私有仓库，仅授权用户可见。
5. 如果本地项目已经存在，建议不要勾选 `Add a README file`。
6. 点击 `Create repository`。

创建完成后，GitHub 会显示远程仓库地址，格式通常如下：

```text
https://github.com/你的用户名/仓库名.git
```

或 SSH 地址：

```text
git@github.com:你的用户名/仓库名.git
```

## 3. 上传已有本地项目

进入你的项目目录：

```bash
cd 项目路径
```

例如：

```bash
cd C:\Users\你的用户名\Desktop\my-project
```

### 3.1 初始化 Git 仓库

如果项目还不是 Git 仓库，运行：

```bash
git init
```

如果项目目录里已经有 `.git` 文件夹，可以跳过这一步。

### 3.2 查看文件状态

```bash
git status
```

### 3.3 添加文件到暂存区

添加全部文件：

```bash
git add .
```

也可以只添加指定文件：

```bash
git add README.md
```

### 3.4 提交文件

```bash
git commit -m "Initial commit"
```

### 3.5 设置主分支名称

推荐使用 `main` 作为默认分支：

```bash
git branch -M main
```

### 3.6 绑定 GitHub 远程仓库

把下面命令中的地址替换成你的 GitHub 仓库地址：

```bash
git remote add origin https://github.com/你的用户名/仓库名.git
```

查看远程仓库是否绑定成功：

```bash
git remote -v
```

### 3.7 推送到 GitHub

```bash
git push -u origin main
```

第一次推送时，GitHub 可能会要求登录或输入访问令牌。

## 4. GitHub 登录与认证

GitHub 已不再支持使用账号密码直接推送代码，常用方式有两种。

### 4.1 使用 HTTPS 和 Personal Access Token

当终端要求输入密码时，不要输入 GitHub 登录密码，而是输入 Personal Access Token。

创建 Token 的位置：

```text
GitHub -> Settings -> Developer settings -> Personal access tokens
```

创建时至少需要勾选仓库相关权限，例如 `repo`。

### 4.2 使用 SSH

生成 SSH 密钥：

```bash
ssh-keygen -t ed25519 -C "你的邮箱@example.com"
```

查看公钥：

```bash
cat ~/.ssh/id_ed25519.pub
```

复制公钥内容，添加到 GitHub：

```text
GitHub -> Settings -> SSH and GPG keys -> New SSH key
```

测试 SSH 是否可用：

```bash
ssh -T git@github.com
```

如果使用 SSH 地址绑定远程仓库：

```bash
git remote add origin git@github.com:你的用户名/仓库名.git
```

## 5. 后续更新代码

以后每次修改项目后，通常按下面流程提交并上传：

```bash
git status
git add .
git commit -m "描述本次修改"
git push
```

如果是第一次推送某个新分支：

```bash
git push -u origin 分支名
```

## 6. 从 GitHub 下载项目

如果要把 GitHub 上的仓库下载到本地：

```bash
git clone https://github.com/你的用户名/仓库名.git
```

进入项目目录：

```bash
cd 仓库名
```

## 7. 常见问题

### 7.1 提示 remote origin already exists

说明已经绑定过远程仓库。可以先查看：

```bash
git remote -v
```

如果地址不对，可以修改：

```bash
git remote set-url origin https://github.com/你的用户名/仓库名.git
```

### 7.2 提示 failed to push some refs

可能是 GitHub 远程仓库里已经有文件，而本地没有同步。可以先拉取：

```bash
git pull origin main --rebase
```

然后再推送：

```bash
git push
```

### 7.3 提示 Authentication failed

常见原因：

- 使用了 GitHub 密码，而不是 Personal Access Token。
- Token 权限不足或已过期。
- SSH key 没有添加到 GitHub。
- 远程仓库地址写错。

### 7.4 不小心上传了不该上传的文件

先把文件加入 `.gitignore`：

```gitignore
.env
node_modules/
bin/
obj/
dist/
```

如果文件已经被 Git 跟踪，需要从 Git 跟踪中移除，但保留本地文件：

```bash
git rm --cached 文件名
git commit -m "Remove ignored file"
git push
```

如果上传了密码、Token、密钥等敏感信息，应立即删除并更换对应密钥。

## 8. 推荐的 .gitignore

不同项目需要不同的 `.gitignore`。可以参考 GitHub 官方模板：

[https://github.com/github/gitignore](https://github.com/github/gitignore)

常见示例：

```gitignore
# 系统文件
.DS_Store
Thumbs.db

# 编辑器
.vscode/
.idea/

# 日志
*.log

# 环境变量
.env
.env.local

# 依赖目录
node_modules/

# 构建输出
dist/
build/
bin/
obj/
```

## 9. 完整命令速查

如果你已经在 GitHub 创建了空仓库，可以在本地项目目录中依次运行：

```bash
git init
git add .
git commit -m "Initial commit"
git branch -M main
git remote add origin https://github.com/你的用户名/仓库名.git
git push -u origin main
```

后续更新：

```bash
git add .
git commit -m "描述本次修改"
git push
```

## 10. 建议流程

日常开发时推荐养成下面习惯：

1. 修改代码前先运行 `git status`。
2. 每完成一个小功能或修复一个问题，就提交一次。
3. 提交信息写清楚本次改动目的。
4. 推送前确认没有上传临时文件、编译产物、密钥或账号信息。
5. 重要项目建议使用分支开发，再通过 Pull Request 合并。

