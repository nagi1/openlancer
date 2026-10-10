<#
.SYNOPSIS
Applies OpenLancer's local fixes in patches/librelancer to the Librelancer submodule.

.DESCRIPTION
The patches stay local. They are never submitted to Librelancer, which does not accept AI-written code.
The submodule stays pinned to the upstream commit in docs/UPSTREAM.md. The patches change only its working tree.

The patches are a stack. Each patch applies on top of all patches before it in name order.
A later patch can change lines that an earlier patch added, so the script does not check a patch alone.
In a temporary index, the script calculates the expected tree after each prefix of the stack.
The prefixes are: no patch, the first patch, the first two patches, and so on.
It compares the working tree with each prefix tree on all paths that the patches touch.
A path that is not in a prefix tree must not exist on disk.
Git hashes each working file with its line-ending conversion, so a CRLF working file matches its LF blob.

When the working tree matches the tree after the first k patches, the script applies only the patches after them.
When the working tree matches no prefix tree, the script stops with an error that names the files that differ.
Run the script after a clone, after each submodule update and after a new patch arrives.

.PARAMETER Check
Report "applied" or "missing" for each patch and change nothing.
#>
param([switch]$Check)

# Not 'Stop': Windows PowerShell can treat a git message on stderr as a fatal error.
# The script checks the exit code of each git call and throws its own errors.
$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$submodule = Join-Path $repo 'external/Librelancer'
$patches = @(Get-ChildItem (Join-Path $repo 'patches/librelancer') -Filter '*.patch' | Sort-Object Name)

# Runs git in the submodule and returns its output lines. A failed git call stops the script.
# Quote '--' in the arguments: PowerShell removes a bare -- from the arguments of a function.
function Invoke-Git {
    & git -C $submodule @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed in $submodule."
    }
}

# $trees[k] is the expected tree after the first k patches. $trees[0] is the pinned commit.
$trees = @()
$index = Join-Path ([IO.Path]::GetTempPath()) "openlancer-patch-stack-$([guid]::NewGuid()).index"
$previousIndex = $env:GIT_INDEX_FILE
try {
    $env:GIT_INDEX_FILE = $index
    Invoke-Git read-tree HEAD
    $trees += Invoke-Git write-tree
    foreach ($patch in $patches) {
        & git -C $submodule apply --cached --whitespace=nowarn $patch.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "$($patch.Name) does not apply on top of the patches before it at the pinned commit. Update the patch."
        }
        $trees += Invoke-Git write-tree
    }
}
finally {
    $env:GIT_INDEX_FILE = $previousIndex
    Remove-Item -LiteralPath $index, "$index.lock" -ErrorAction SilentlyContinue
}

# The paths that the patches touch: each path that changes from one prefix tree to the next.
$paths = @()
for ($k = 1; $k -lt $trees.Count; $k++) {
    $paths += @(Invoke-Git diff-tree -r --no-renames --name-only $trees[$k - 1] $trees[$k])
}
$paths = @($paths | Sort-Object -Unique)

# The blob hash of each path in the working tree. A path that is not on disk has no entry.
$actual = @{}
$onDisk = @($paths | Where-Object { Test-Path -LiteralPath (Join-Path $submodule $_) -PathType Leaf })
if ($onDisk.Count -gt 0) {
    $hashes = @(Invoke-Git hash-object '--' @onDisk)
    for ($i = 0; $i -lt $onDisk.Count; $i++) {
        $actual[$onDisk[$i]] = $hashes[$i]
    }
}

# $differences[k] is the list of paths where the working tree is not equal to $trees[k].
$differences = @()
foreach ($tree in $trees) {
    $expected = @{}
    if ($paths.Count -gt 0) {
        foreach ($line in @(Invoke-Git ls-tree -r $tree '--' @paths)) {
            # Format: <mode> blob <hash><TAB><path>
            $entry, $path = $line -split "`t", 2
            $expected[$path] = ($entry -split ' ')[2]
        }
    }
    $differences += , @($paths | Where-Object { $expected[$_] -ne $actual[$_] })
}

# The prefix with the most patches wins when two prefix trees are equal on all paths.
$appliedCount = -1
for ($k = $trees.Count - 1; $k -ge 0; $k--) {
    if ($differences[$k].Count -eq 0) {
        $appliedCount = $k
        break
    }
}
if ($appliedCount -lt 0) {
    # Name each nearest prefix state (fewest different files) and the files that are different from it.
    $fewest = ($differences | ForEach-Object { $_.Count } | Measure-Object -Minimum).Minimum
    $nearest = for ($k = 0; $k -lt $trees.Count; $k++) {
        if ($differences[$k].Count -eq $fewest) {
            $state = if ($k -eq 0) { 'the pinned commit with no patch' } else { "the state after $($patches[$k - 1].Name)" }
            "Different from ${state}:`n    " + ($differences[$k] -join "`n    ")
        }
    }
    throw ("The Librelancer working tree is not in the state of any prefix of the patches.`n  " +
        ($nearest -join "`n  ") +
        "`nMake these files equal to one prefix state. Then run the script again. " +
        "For example, move a local change into a new patch.")
}

for ($i = 0; $i -lt $patches.Count; $i++) {
    $name = $patches[$i].Name
    if ($i -lt $appliedCount) {
        Write-Output "applied   $name"
    }
    elseif ($Check) {
        Write-Output "missing   $name"
    }
    else {
        & git -C $submodule apply --whitespace=nowarn $patches[$i].FullName
        if ($LASTEXITCODE -ne 0) {
            throw "git apply failed for $name."
        }
        Write-Output "applied   $name (now)"
    }
}
