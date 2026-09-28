import { Box, ButtonBase } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../../../context/AuthContext';
import { NEU_TRANSITION, neuFocus, neuInset, neuKey } from './neumorphic';

/**
 * The dashboard's doors: Overview is this screen; every other tab opens the page that holds that
 * subject in depth. A tab shows only when the reader may open its page, so no tab leads to a
 * "not allowed" screen.
 */
const TABS: readonly { key: string; label: string; to: string | null; module: string | null }[] = [
  { key: 'overview', label: 'Overview', to: null, module: null },
  { key: 'today', label: 'Today', to: '/sales/today', module: 'Leads' },
  { key: 'reps', label: 'Reps', to: '/sales/performance', module: 'Dashboard' },
  { key: 'customers', label: 'Customers', to: '/customers', module: 'Customers' },
  { key: 'suppliers', label: 'Suppliers', to: '/suppliers', module: 'Suppliers' },
  { key: 'brands', label: 'Brands asked for', to: '/analytics/brand-demand', module: 'Leads' },
  { key: 'deadlines', label: 'Deadlines', to: '/analytics/deadlines', module: 'Leads' },
  { key: 'documents', label: 'Documents to check', to: '/procurement/extraction/review', module: 'Leads' },
];

export default function DashboardTabs() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const tabs = TABS.filter(t => t.module === null || hasPermission(t.module));
  return (
    <Box component="nav" aria-label="Dashboard sections" sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mb: 1.5 }}>
      {tabs.map(t => {
        const current = t.to === null;
        return (
          <ButtonBase
            key={t.key}
            aria-current={current ? 'page' : undefined}
            onClick={() => { if (t.to) navigate(t.to); }}
            sx={theme => ({
              ...neuKey(theme.palette.mode), ...neuFocus, ...NEU_TRANSITION,
              px: 1.75, height: 34, borderRadius: '12px', fontSize: 13, fontWeight: 700,
              color: current ? 'var(--nx-glance-seal-ink)' : 'text.primary',
              ...(current ? { boxShadow: neuInset(theme.palette.mode, 2) } : {}),
            })}
          >
            {t.label}
          </ButtonBase>
        );
      })}
    </Box>
  );
}
