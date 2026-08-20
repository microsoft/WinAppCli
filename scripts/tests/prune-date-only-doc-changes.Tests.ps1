#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for scripts/prune-date-only-doc-changes.ps1.

    port-mslearn-docs.ps1 restamps ms.date on every page, so without pruning a
    release PR is mostly date-only churn (19 of 26 files on the v0.6.2 PR). These
    tests pin the behaviour that keeps that churn out of the commit: date-only
    edits are discarded, everything else survives.
#>

BeforeAll {
    $script:PruneScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'prune-date-only-doc-changes.ps1'

    function New-DocsFixture {
        <#
            A git repo with three committed pages, then a simulated port run:
            ms.date bumped everywhere, one body edited, one page added.
        #>
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("prune-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $root 'docs\sub') -Force | Out-Null

        Push-Location $root
        try {
            & git init -q .
            & git config user.email 'test@example.com'
            & git config user.name 'test'

            Set-Content (Join-Path $root 'docs\dateonly.md') "---`r`ntitle: A`r`nms.date: 01/01/2020`r`n---`r`n`r`n# A`r`nUnchanged body.`r`n"
            Set-Content (Join-Path $root 'docs\realchange.md') "---`r`ntitle: B`r`nms.date: 01/01/2020`r`n---`r`n`r`n# B`r`nOld body.`r`n"
            Set-Content (Join-Path $root 'docs\sub\nested.md') "---`r`ntitle: C`r`nms.date: 01/01/2020`r`n---`r`n`r`n# C`r`nUnchanged body.`r`n"
            Set-Content (Join-Path $root 'docs\toc.yml') "items: 1`r`n"

            & git add -A
            & git commit -qm 'baseline'

            # Simulated port run
            Set-Content (Join-Path $root 'docs\dateonly.md') "---`r`ntitle: A`r`nms.date: 08/20/2026`r`n---`r`n`r`n# A`r`nUnchanged body.`r`n"
            Set-Content (Join-Path $root 'docs\realchange.md') "---`r`ntitle: B`r`nms.date: 08/20/2026`r`n---`r`n`r`n# B`r`nNew body.`r`n"
            Set-Content (Join-Path $root 'docs\sub\nested.md') "---`r`ntitle: C`r`nms.date: 08/20/2026`r`n---`r`n`r`n# C`r`nUnchanged body.`r`n"
            Set-Content (Join-Path $root 'docs\toc.yml') "items: 2`r`n"
            Set-Content (Join-Path $root 'docs\brandnew.md') "---`r`ntitle: D`r`nms.date: 08/20/2026`r`n---`r`n`r`n# D`r`nBrand new.`r`n"
        }
        finally { Pop-Location }

        return $root
    }

    function Get-ChangedPaths {
        param([string]$Root)
        Push-Location $Root
        try { return @(& git diff HEAD --name-only | Where-Object { $_ }) }
        finally { Pop-Location }
    }
}

Describe 'prune-date-only-doc-changes' {
    Context 'unstaged changes' {
        BeforeAll {
            $script:Root = New-DocsFixture
            & pwsh -NoProfile -File $script:PruneScript -RepoPath $script:Root -PathSpec 'docs' *>&1 | Out-Null
            $script:Exit = $LASTEXITCODE
            $script:Changed = Get-ChangedPaths $script:Root
        }
        AfterAll { Remove-Item $script:Root -Recurse -Force -ErrorAction SilentlyContinue }

        It 'exits 0' { $script:Exit | Should -Be 0 }

        It 'discards a page whose only change is ms.date' {
            $script:Changed | Should -Not -Contain 'docs/dateonly.md'
        }

        It 'discards date-only pages in nested directories' {
            $script:Changed | Should -Not -Contain 'docs/sub/nested.md'
        }

        It 'keeps a page whose body changed, date bump notwithstanding' {
            $script:Changed | Should -Contain 'docs/realchange.md'
        }

        It 'keeps non-markdown files such as toc.yml' {
            $script:Changed | Should -Contain 'docs/toc.yml'
        }

        It 'leaves a newly added page in place' {
            Join-Path $script:Root 'docs\brandnew.md' | Should -Exist
        }
    }

    Context 'already staged changes' {
        BeforeAll {
            $script:Root2 = New-DocsFixture
            Push-Location $script:Root2
            & git add -A
            Pop-Location
            & pwsh -NoProfile -File $script:PruneScript -RepoPath $script:Root2 -PathSpec 'docs' *>&1 | Out-Null
            $script:Changed2 = Get-ChangedPaths $script:Root2
        }
        AfterAll { Remove-Item $script:Root2 -Recurse -Force -ErrorAction SilentlyContinue }

        It 'drops date-only pages out of the staged set too' {
            $script:Changed2 | Should -Not -Contain 'docs/dateonly.md'
            $script:Changed2 | Should -Contain 'docs/realchange.md'
        }
    }

    Context 'WhatIf' {
        BeforeAll {
            $script:Root3 = New-DocsFixture
            & pwsh -NoProfile -File $script:PruneScript -RepoPath $script:Root3 -PathSpec 'docs' -WhatIf *>&1 | Out-Null
            $script:Changed3 = Get-ChangedPaths $script:Root3
        }
        AfterAll { Remove-Item $script:Root3 -Recurse -Force -ErrorAction SilentlyContinue }

        It 'reports without modifying the working tree' {
            $script:Changed3 | Should -Contain 'docs/dateonly.md'
        }
    }

    Context 'nothing to do' {
        BeforeAll {
            $script:Root4 = New-DocsFixture
            Push-Location $script:Root4
            & git checkout -q -- .
            & git clean -qfd
            Pop-Location
            & pwsh -NoProfile -File $script:PruneScript -RepoPath $script:Root4 -PathSpec 'docs' *>&1 | Out-Null
            $script:Exit4 = $LASTEXITCODE
        }
        AfterAll { Remove-Item $script:Root4 -Recurse -Force -ErrorAction SilentlyContinue }

        It 'exits 0 on a clean tree' { $script:Exit4 | Should -Be 0 }
    }
}
