$publishDir = 'F:\SQL_DAM\installer\publish'
$outputFile = 'F:\SQL_DAM\installer\Files.wxs'

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="PublishedFiles" Directory="INSTALLFOLDER">')

$files = Get-ChildItem -Recurse -File $publishDir
$idx = 0

foreach ($file in $files) {
    if ($file.Name -eq 'SqlDam.Web.exe' -or $file.Name -eq 'SqlDam.Desktop.exe') {
        continue
    }
    $idx++
    $relPath = $file.FullName.Substring($publishDir.Length + 1)
    # Ensure source uses valid path syntax
    $sourcePath = "publish\" + $relPath
    $compId = "cmp_$idx"
    $fileId = "fil_$idx"

    # Handle subdirectories if needed, or place relative to INSTALLFOLDER
    # WiX v4 supports relative target subdirectories using DirectoryRef or subfolders
    $subDir = [System.IO.Path]::GetDirectoryName($relPath)
    if ([string]::IsNullOrWhiteSpace($subDir)) {
        [void]$sb.AppendLine("      <Component Id=""$compId"" Guid=""*"" Bitness=""always64"">")
        [void]$sb.AppendLine("        <File Id=""$fileId"" Source=""$sourcePath"" />")
        [void]$sb.AppendLine('      </Component>')
    } else {
        # WiX v4 supports Subdirectory attribute on Component
        $subDirClean = $subDir.Replace('/', '\')
        [void]$sb.AppendLine("      <Component Id=""$compId"" Guid=""*"" Bitness=""always64"" Subdirectory=""$subDirClean"">")
        [void]$sb.AppendLine("        <File Id=""$fileId"" Source=""$sourcePath"" />")
        [void]$sb.AppendLine('      </Component>')
    }
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

[System.IO.File]::WriteAllText($outputFile, $sb.ToString())
Write-Host "Generated $idx component definitions in $outputFile"
