#Requires -Version 7.0

# Pester smoke tests for scripts/check-mslearn-markers.ps1. Builds a throwaway
# git repo in a temp directory so we can exercise the marker check end-to-end
# without depending on any real branch or remote.

Describe 'check-mslearn-markers.ps1' {

    BeforeAll {
        $script:scriptPath = (Resolve-Path "$PSScriptRoot\..\..\scripts\check-mslearn-markers.ps1").Path
        $script:marker = '<!-- mslearn: true -->'

        function New-TempRepo {
            $dir = Join-Path ([IO.Path]::GetTempPath()) "mslearn-test-$([guid]::NewGuid())"
            New-Item -ItemType Directory -Path $dir | Out-Null
            Push-Location $dir
            try {
                git init -q -b main
                git config user.email 'test@example.com'
                git config user.name  'Test'
            } finally {
                Pop-Location
            }
            return $dir
        }

        function Commit-Doc {
            param([string]$Repo, [string]$RelPath, [string]$Content, [string]$Message)
            $full = Join-Path $Repo $RelPath
            New-Item -ItemType Directory -Path (Split-Path $full) -Force | Out-Null
            Set-Content -Path $full -Value $Content -NoNewline
            Push-Location $Repo
            try {
                git add -- $RelPath | Out-Null
                git commit -q -m $Message --allow-empty | Out-Null
                return (git rev-parse HEAD).Trim()
            } finally {
                Pop-Location
            }
        }

        function Invoke-Check {
            param([string]$Repo, [string]$BaseRef)
            Push-Location $Repo
            try {
                $out = & pwsh -NoProfile -File $script:scriptPath -BaseRef $BaseRef 2>&1
                return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n") }
            } finally {
                Pop-Location
            }
        }

        $script:repos = New-Object System.Collections.Generic.List[string]
    }

    AfterAll {
        foreach ($r in $script:repos) {
            if (Test-Path $r) { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $r }
        }
    }

    It 'passes when marker is preserved on HEAD' {
        $repo = New-TempRepo; $script:repos.Add($repo)
        $base = Commit-Doc -Repo $repo -RelPath 'docs/a.md' -Content "$script:marker`n# A" -Message 'base'
        Commit-Doc -Repo $repo -RelPath 'docs/a.md' -Content "$script:marker`n# A v2" -Message 'edit' | Out-Null

        $result = Invoke-Check -Repo $repo -BaseRef $base
        $result.ExitCode | Should -Be 0
        $result.Output   | Should -Match 'no mslearn markers were dropped'
    }

    It 'fails when marker is stripped from an existing doc' {
        $repo = New-TempRepo; $script:repos.Add($repo)
        $base = Commit-Doc -Repo $repo -RelPath 'docs/b.md' -Content "$script:marker`n# B" -Message 'base'
        Commit-Doc -Repo $repo -RelPath 'docs/b.md' -Content "# B (no marker)" -Message 'strip marker' | Out-Null

        $result = Invoke-Check -Repo $repo -BaseRef $base
        $result.ExitCode | Should -Be 1
        $result.Output   | Should -Match 'docs/b\.md'
    }

    It 'passes when a marked doc is renamed and marker is preserved' {
        $repo = New-TempRepo; $script:repos.Add($repo)
        $content = "$script:marker`n# Stable content used to make rename detection trigger.`nLine.`nLine.`nLine.`nLine."
        $base = Commit-Doc -Repo $repo -RelPath 'docs/old.md' -Content $content -Message 'base'
        Push-Location $repo
        try {
            git mv docs/old.md docs/new.md | Out-Null
            git commit -q -m 'rename' | Out-Null
        } finally { Pop-Location }

        $result = Invoke-Check -Repo $repo -BaseRef $base
        $result.ExitCode | Should -Be 0
        $result.Output   | Should -Match 'renamed to .*new\.md.* with marker preserved'
    }

    It 'fails when a marked doc is deleted' {
        $repo = New-TempRepo; $script:repos.Add($repo)
        $base = Commit-Doc -Repo $repo -RelPath 'docs/c.md' -Content "$script:marker`n# C" -Message 'base'
        Push-Location $repo
        try {
            git rm -q docs/c.md | Out-Null
            git commit -q -m 'delete' | Out-Null
        } finally { Pop-Location }

        $result = Invoke-Check -Repo $repo -BaseRef $base
        $result.ExitCode | Should -Be 1
        $result.Output   | Should -Match 'docs/c\.md'
    }

    It 'reports newly added marked docs without failing' {
        $repo = New-TempRepo; $script:repos.Add($repo)
        $base = Commit-Doc -Repo $repo -RelPath 'docs/keep.md' -Content "$script:marker`n# Keep" -Message 'base'
        Commit-Doc -Repo $repo -RelPath 'docs/new.md'  -Content "$script:marker`n# New"  -Message 'add new' | Out-Null

        $result = Invoke-Check -Repo $repo -BaseRef $base
        $result.ExitCode | Should -Be 0
        $result.Output   | Should -Match '\+\s+docs/new\.md'
    }
}
