@{
    # E2E scenario manifest (ADR-038 D1/D4, WP4 #243). One entry per scenario
    # script in this directory. Keys:
    #   Name            - scenario script basename (<Name>.ps1)
    #   Tags            - any of: smoke | full | perf | requires-ad
    #   TimeoutSec      - runner watchdog; on fire the child is killed and the
    #                     app + msedgewebview2 descendants are killed by PPID walk
    #   RetrySignatures - wildcard patterns; a failed scenario is retried ONCE
    #                     iff its result signature matches one (default: none -
    #                     never blanket retries, the build.ps1 signature-gate
    #                     precedent)
    Scenarios = @(
        @{
            Name            = 'launch-render'
            Tags            = @('smoke', 'full')
            TimeoutSec      = 240
            RetrySignatures = @()
        }
        @{
            Name            = 'back-nav'
            Tags            = @('smoke', 'full')
            TimeoutSec      = 420
            RetrySignatures = @()
        }
        @{
            Name            = 'audit-run-persist'
            Tags            = @('smoke', 'full')
            TimeoutSec      = 300
            RetrySignatures = @()
        }
        @{
            Name            = 'step-swap-churn'
            Tags            = @('smoke', 'full')
            TimeoutSec      = 360
            RetrySignatures = @()
        }
        @{
            Name            = 'selection-sync'
            Tags            = @('smoke', 'full')
            TimeoutSec      = 300
            RetrySignatures = @()
        }
        @{
            Name            = 'audit-zero-drift'
            Tags            = @('full')
            TimeoutSec      = 360
            RetrySignatures = @()
        }
        @{
            Name            = 'settings-rethread'
            Tags            = @('full')
            TimeoutSec      = 300
            RetrySignatures = @()
        }
    )
}
