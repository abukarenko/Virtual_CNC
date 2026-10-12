Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
app = files.BuildPath(root, "VirtualCNC.exe")
If Not files.FileExists(app) Then app = files.BuildPath(root, "artifacts\app\VirtualCnc.App.exe")
If files.FileExists(app) Then
    shell.CurrentDirectory = root
    shell.Run Chr(34) & app & Chr(34), 1, False
Else
    MsgBox "Build the application first using scripts\Build.ps1.", 48, "Virtual CNC"
End If
