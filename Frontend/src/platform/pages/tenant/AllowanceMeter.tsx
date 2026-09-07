import { Alert, Box, Chip, TextField, Typography } from '@mui/material'
import Stack from '../../components/Flex'
import type { TenantAiPolicy } from '../../types'

/**
 * The monthly AI allowance, set in the unit the customer signed for.
 *
 * <b>What this replaces.</b> Three raw number boxes — "Monthly soft token limit", "Monthly hard
 * token limit", "Document token limit" — asked a salesperson for a figure in tokens. Nobody sells
 * tokens, so the honest answers were "leave it blank" (unbounded spend, which the readiness report
 * now warns about) or a round number that looks big and means nothing. The ledger still enforces
 * tokens, because tokens are the only unit it has at the moment of enforcement; this converts.
 *
 * The conversion factor is served by the API rather than kept here, so the console and the ledger
 * cannot drift apart.
 *
 * <b>Why presets and a number, and no slider.</b> A slider was the obvious control and it was the
 * wrong one twice over. Across a range of 1 to 10,000 documents it cannot land on a figure anybody
 * negotiated, so every real answer came from the field beside it anyway. And MUI's Slider is not
 * used anywhere else in the product: pulling it in added 19.5 KB to the shared vendor chunk that
 * EVERY page load fetches — the salesperson opening a quote pays for a control on one admin
 * dialog — and put the build 18,891 bytes over a budget that had 641 bytes of headroom.
 */
export default function AllowanceMeter({
  policy,
  documents,
  uncapped,
  onChange,
}: {
  policy: TenantAiPolicy
  documents: number
  uncapped: boolean
  onChange: (next: { documents: number; uncapped: boolean }) => void
}) {
  const perDocument = policy.tokensPerDocument || 12_000
  const presets = policy.allowancePresets?.length ? policy.allowancePresets : [100, 500, 2_000, 10_000]
  const tokens = documents * perDocument

  return (
    <Stack spacing={1.5}>
      <Box>
        <Typography sx={{ fontWeight: 700 }}>How many documents a month?</Typography>
        <Typography variant="body2" color="text.secondary">
          What the customer is signed up for. The ledger enforces this as a token ceiling — it is
          the only unit available at the moment a call is refused — and the conversion is shown
          below so nothing is hidden.
        </Typography>
      </Box>

      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
        {presets.map((preset) => (
          <Chip
            key={preset}
            label={`${preset.toLocaleString()} docs`}
            color={!uncapped && documents === preset ? 'primary' : 'default'}
            variant={!uncapped && documents === preset ? 'filled' : 'outlined'}
            onClick={() => onChange({ documents: preset, uncapped: false })}
          />
        ))}
        {/* Never a preset chip beside the others: unlimited spend is a different KIND of answer,
            and making it one click away from "500 docs" is how it gets chosen by accident. */}
        <Chip
          label="No ceiling"
          color={uncapped ? 'warning' : 'default'}
          variant={uncapped ? 'filled' : 'outlined'}
          onClick={() => onChange({ documents, uncapped: true })}
        />
      </Stack>

      {!uncapped && (
        <Stack direction="row" spacing={2} alignItems="center" sx={{ flexWrap: 'wrap' }}>
          <TextField
            size="small"
            type="number"
            label="Documents / month"
            value={documents}
            onChange={(event) => onChange({ documents: Number(event.target.value), uncapped: false })}
            sx={{ maxWidth: 190 }}
            slotProps={{ htmlInput: { min: 1, step: 50, 'aria-label': 'Documents per month' } }}
          />
          <Typography variant="body2" color="text.secondary">
            = <strong>{tokens.toLocaleString()}</strong> tokens a month, at {perDocument.toLocaleString()} per document.
          </Typography>
        </Stack>
      )}

      {uncapped && (
        <Alert severity="warning" role="status" sx={{ py: 0.5 }}>
          <Typography variant="body2" sx={{ fontWeight: 650 }}>Unlimited AI spend for this tenant.</Typography>
          <Typography variant="body2">
            Nothing stops a runaway. An unattended bulk import or a retry loop on one bad document
            can spend for hours before anyone looks — and the tenant will carry a standing warning
            on this tab until a number is set.
          </Typography>
        </Alert>
      )}

      {/* The one fact that stops an operator over-buying: their commonest document costs nothing. */}
      <Alert severity="info" icon={false} role="status" sx={{ py: 0.5 }}>
        <Typography variant="body2">
          Documents that match a known layout — Aramco bid lists, recognised spreadsheets — are read
          deterministically and spend <strong>no tokens at all</strong>. This allowance only governs
          documents that have to go to the model.
        </Typography>
      </Alert>

      <Typography variant="caption" color="text.secondary">
        {policy.deploymentRateSummary}
      </Typography>
    </Stack>
  )
}
