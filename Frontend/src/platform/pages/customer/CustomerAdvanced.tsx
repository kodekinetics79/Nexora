import { useEffect, useMemo, useState, type ReactElement } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Box, Typography } from '@mui/material';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import type { Tenant } from '../../types';
import ActivationPolicyPanel from '../tenant/ActivationPolicyPanel';
import CommercialTab from '../tenant/CommercialTab';
import ModulesTab from '../tenant/ModulesTab';
import SupportTab from '../tenant/SupportTab';
import ProvisioningDiagnosticsTab from '../tenant/ProvisioningDiagnosticsTab';
import AuditTab from '../tenant/AuditTab';
import AiGovernanceTab from '../tenant/AiGovernanceTab';
import DataStorageTab from '../tenant/DataStorageTab';
import LifecycleTab from '../tenant/LifecycleTab';

/**
 * THE REST OF A CUSTOMER, on the customer's own page.
 *
 * <b>What this replaces, and why it had to go.</b> The same customer had two consoles. Opening
 * `/platform/customers/1` gave the editable page; opening `/platform/tenants/1` gave ten peer tabs.
 * Same record, same operator, two products — and which one you got depended on which link you
 * happened to click. An operator cannot build one mental model of a screen that is two screens, so
 * every fact learned on one had to be re-learned on the other. That is the training cost this
 * console exists to avoid, and it was self-inflicted: the tabbed page was left standing when the
 * customer page was added beside it.
 *
 * <b>Why a disclosure list rather than tabs.</b> Ten peer tabs assert that ten things are equally
 * likely to be wanted, which is false — an operator opens Activation constantly and Data residency
 * a handful of times a year. Tabs also hide their own contents: nothing on the strip says whether
 * there is anything behind a tab worth opening, so the only way to find out is to open all ten.
 * Each section here states what it is for in a sentence, so the decision to open it is made from
 * the list rather than from clicking.
 *
 * <b>Nothing is mounted until it is opened.</b> Every panel below fetches its own data. Rendering
 * them eagerly would fire eight queries on a page whose common use is reading the top of it, so
 * `unmountOnExit` keeps them inert until asked for — the same behaviour tabs gave, without the
 * tabs.
 *
 * <b>Deep links still work.</b> Support tickets carry `?tab=lifecycle` URLs. Those keys are mapped
 * onto sections here and opened on arrival, so a link written a month ago lands on the thing it
 * named instead of the top of a page.
 */
interface Section {
  key: string;
  title: string;
  /** When somebody would open this. The reason the list is readable without clicking. */
  purpose: string;
  visible: boolean;
  /** A number worth showing on the pill, or null when the page does not honestly have one. */
  badge: number | null;
  badgeTone?: 'warning' | 'neutral';
  render: () => ReactElement;
}

export default function CustomerAdvanced(
  { tenant, outstandingCount = 0 }: { tenant: Tenant; outstandingCount?: number },
) {
  const permissions = usePlatformPermissions();
  const [searchParams, setSearchParams] = useSearchParams();
  const [open, setOpen] = useState<string | null>(null);

  const sections = useMemo<Section[]>(() => [
    {
      key: 'activation',
      title: 'Getting this customer live',
      purpose:
        'Every control the server checks before it will let this customer start working, and what is still outstanding.',
      visible: true,
      badge: outstandingCount > 0 ? outstandingCount : null,
      badgeTone: 'warning',
      render: () => <ActivationPolicyPanel tenant={tenant} />,
    },
    {
      key: 'commercial',
      title: 'Plan and how they are billed',
      purpose: 'Their plan, whether they are billable or on trial, and which rate card their statements are computed against.',
      visible: true,
      badge: null,
      render: () => <CommercialTab tenant={tenant} />,
    },
    {
      key: 'entitlements',
      title: 'What they are allowed to use',
      purpose: 'Which modules are switched on for this customer, and the seat, document and extraction limits their plan carries.',
      visible: true,
      badge: null,
      render: () => <ModulesTab tenant={tenant} />,
    },
    {
      key: 'support',
      title: 'Their support tickets',
      purpose: 'Everything raised by or about this customer, and who it is waiting on.',
      visible: permissions.canAdministerTenants,
      badge: null,
      render: () => <SupportTab tenant={tenant} />,
    },
    {
      key: 'provisioning',
      title: 'How their workspace was built',
      purpose: 'The provisioning run that created this customer, step by step — where to look when something did not come out right.',
      visible: true,
      badge: null,
      render: () => <ProvisioningDiagnosticsTab tenant={tenant} />,
    },
    {
      key: 'audit',
      title: 'Everything anyone has done to this customer',
      purpose: 'The audit trail: who changed what, when, and the reason they gave.',
      visible: true,
      badge: null,
      render: () => <AuditTab tenant={tenant} />,
    },
    {
      key: 'ai-governance',
      title: 'AI settings and consent',
      purpose: 'What this customer has agreed their data may be used for, and which AI features that permits.',
      visible: permissions.isOwner,
      badge: null,
      render: () => <AiGovernanceTab tenant={tenant} />,
    },
    {
      key: 'data-storage',
      title: 'Where their data lives',
      purpose: 'Hosting region, backup policy, and the evidence that the contracted data boundary is actually being held.',
      visible: permissions.isOwner,
      badge: null,
      render: () => <DataStorageTab tenant={tenant} />,
    },
    {
      key: 'lifecycle',
      title: 'Ending this customer',
      purpose: 'Offboarding, data retention and deletion. Irreversible once started, so it asks before each step.',
      visible: permissions.isOwner,
      badge: null,
      render: () => <LifecycleTab tenant={tenant} />,
    },
  ], [tenant, outstandingCount, permissions.canAdministerTenants, permissions.isOwner]);

  const available = sections.filter((entry) => entry.visible);

  // A `?tab=` from an older link, or `?section=` from a current one, decides what is open on
  // arrival. Nothing opens on its own: a page whose default state is one long panel is a page
  // nobody can scan.
  useEffect(() => {
    const requested = searchParams.get('section') ?? searchParams.get('tab');
    if (requested && available.some((entry) => entry.key === requested)) {
      setOpen(requested);
      return;
    }
    // Deliberately NOT auto-expanded. The activation panel renders all fifteen controls, passes
    // included, so opening it by default made the page 5,800px of mostly-green checks — the exact
    // "scattered" complaint this redesign was supposed to answer. Discovery is handled above the
    // fold instead, by a button that names the count and opens this section.
  }, [searchParams, tenant.status]); // eslint-disable-line react-hooks/exhaustive-deps

  const toggle = (key: string) => {
    const next = open === key ? null : key;
    setOpen(next);
    const params = new URLSearchParams(searchParams);
    if (next) params.set('section', next); else params.delete('section');
    params.delete('tab'); // the old key is honoured on arrival, never written back
    setSearchParams(params, { replace: true });
  };

  const selected = available.find((entry) => entry.key === open) ?? null;

  return (
    <Box sx={{ mt: 4 }} id="customer-advanced">
      <Typography sx={{ fontWeight: 800, fontSize: 18, mb: 0.5 }}>Everything else</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5, maxWidth: '72ch' }}>
        Pick one. Only the section you choose is loaded, and the page stays one screen either way.
      </Typography>

      {/*
        A PILL ROW, NOT NINE STACKED PANELS.
        The accordion this replaces put nine headers and nine purpose sentences permanently on the
        page — around 800px of chrome before a single panel was opened, which is the "scroll down
        to infinity" complaint. A single row of pills costs one line and shows the same nine
        choices at once.

        It is a tablist in ARIA terms and is built as one deliberately: one of a set selects what
        is shown below, which is what `tab` / `tabpanel` / `aria-selected` mean, and it gives
        arrow-key movement and a screen-reader announcement for free. The objection to the tab
        STRIP this console used to have was never the semantics — it was that a bare tab told you
        nothing about whether anything was behind it. The count on the pill answers that.
      */}
      <Box
        role="tablist"
        aria-label="Customer sections"
        sx={{
          display: 'flex', flexWrap: 'wrap', gap: 0.75, p: 0.75,
          borderRadius: 999, bgcolor: 'action.hover',
        }}
      >
        {available.map((entry) => {
          const active = open === entry.key;
          return (
            <Box
              key={entry.key}
              component="button"
              type="button"
              role="tab"
              aria-selected={active}
              aria-controls={`section-${entry.key}`}
              onClick={() => toggle(entry.key)}
              sx={{
                display: 'inline-flex', alignItems: 'center', gap: 0.75,
                px: 1.75, py: 0.75, border: 0, borderRadius: 999, cursor: 'pointer',
                font: 'inherit', fontSize: 13.5, fontWeight: 700,
                color: active ? 'text.primary' : 'text.secondary',
                bgcolor: active ? 'background.paper' : 'transparent',
                boxShadow: active ? '0 1px 3px rgba(0,0,0,0.12)' : 'none',
                '&:hover': { color: 'text.primary' },
              }}
            >
              {entry.title}
              {/*
                Only counts that are already in hand. Every other panel fetches its own data, and a
                pill cannot promise a number the page would have to open the panel to learn — a
                fabricated or perpetually-zero badge is worse than none.
              */}
              {entry.badge !== null && (
                <Box
                  component="span"
                  sx={{
                    px: 0.75, borderRadius: 999, fontSize: 12, fontWeight: 800,
                    color: entry.badgeTone === 'warning' ? 'warning.dark' : 'text.secondary',
                    bgcolor: entry.badgeTone === 'warning' ? 'warning.light' : 'action.selected',
                  }}
                >
                  {entry.badge}
                </Box>
              )}
            </Box>
          );
        })}
      </Box>

      {selected ? (
        <Box id={`section-${selected.key}`} role="tabpanel" sx={{ mt: 2.5 }}>
          {/* The sentence that used to sit on every collapsed header, shown for the one in view. */}
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: '80ch' }}>
            {selected.purpose}
          </Typography>
          {selected.render()}
        </Box>
      ) : (
        <Typography variant="body2" color="text.disabled" sx={{ mt: 2 }}>
          Nothing is open. Choose a section above.
        </Typography>
      )}
    </Box>
  );
}
