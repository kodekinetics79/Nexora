import React, { useEffect, useState } from 'react';
import {
  Box,
  Typography,
  TextField,
  Button,
  IconButton,
  InputAdornment,
  Alert,
  Select,
  MenuItem,
  FormControl,
  InputLabel,
  CircularProgress,
} from '@mui/material';
import {
  MailOutlined as MailIcon,
  LockOutlined as LockIcon,
  LightMode as SunIcon,
  DarkMode as MoonIcon,
  Visibility,
  VisibilityOff,
  CheckCircleOutlined as CheckIcon,
  VerifiedUserOutlined as IntegrityIcon,
  SettingsOutlined as SettingsIcon,
} from '@mui/icons-material';
import styled from '@emotion/styled';
import { useAuth } from '../../context/AuthContext';
import { useAppTheme } from '../../context/ThemeContext';
import Branding from '../../components/common/Branding';
import axiosInstance from '../../api/axiosInstance';
import { Link, useNavigate } from 'react-router-dom';
import userService, { type MePermissionsResponse } from '../../api/services/userService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { MAIN_CONTENT_ID } from '../../components/layout/SkipLink';
import { INBOX_ROOT } from '../../components/layout/navCatalog';

/** Ties the failure Alert to the fields it describes (SC 3.3.1 / SC 3.3.3). */
const LOGIN_ERROR_ID = 'login-error';
const visuallyHidden = {
  position: 'absolute',
  width: 1,
  height: 1,
  p: 0,
  m: -1,
  overflow: 'hidden',
  clip: 'rect(0 0 0 0)',
  whiteSpace: 'nowrap',
  border: 0,
} as const;

const Container = styled.div<{ mode: string }>`
  min-height: 100vh;
  min-height: 100dvh;
  width: 100%;
  box-sizing: border-box;
  background: ${props => props.mode === 'dark' ? '#07111f' : '#f5f7fa'};
  font-family: ${(props: any) => props.theme.typography.fontFamily};
  color-scheme: ${props => props.mode};
`;

const LoginShell = styled.div`
  min-height: 100vh;
  min-height: 100dvh;
  display: grid;
  grid-template-columns: minmax(0, 63%) minmax(420px, 37%);
  overflow: hidden;

  @media (max-width: 1100px) {
    grid-template-columns: 1fr;
    overflow: visible;
  }
`;

const EvidencePanel = styled.section`
  min-width: 0;
  padding: clamp(28px, 3vw, 48px);
  background: #08172a;
  color: #f8fafc;
  display: flex;
  flex-direction: column;
  border-right: 1px solid #283b58;

  @media (max-width: 1100px) {
    min-height: 290px;
    padding: 24px 32px;
    border-right: 0;
    border-bottom: 1px solid #283b58;
  }
  @media (max-width: 600px) {
    min-height: 188px;
    padding: 16px 20px;
  }
`;

const FormSection = styled.main<{ mode: string }>`
  min-width: 0;
  padding: clamp(32px, 3vw, 52px);
  background: ${props => props.mode === 'dark' ? '#0f1b2d' : '#f8f8f8'};
  color: ${props => props.mode === 'dark' ? '#f8fafc' : '#0b172a'};
  display: flex;
  flex-direction: column;
  justify-content: center;
  position: relative;

  @media (max-width: 1100px) {
    min-height: calc(100dvh - 290px);
  }

  @media (max-width: 600px) {
    min-height: calc(100dvh - 188px);
    padding: 28px 20px 24px;
    justify-content: flex-start;
  }
`;

const StyledTextField = styled(TextField)<{ mode?: string }>(({ theme, mode }: any) => ({
  '& .MuiOutlinedInput-root': {
    minHeight: 58,
    backgroundColor: mode === 'dark' ? '#0b172a' : '#ffffff',
    borderRadius: 8,
    transition: 'border-color 160ms ease-out, box-shadow 160ms ease-out',
    '& fieldset': { borderColor: mode === 'dark' ? '#52627a' : '#aeb8c7' },
    '&:hover fieldset': { borderColor: mode === 'dark' ? '#8fa1b8' : '#65758b' },
    '&.Mui-focused': {
      boxShadow: `0 0 0 3px ${theme.palette.primary.main}24`,
    },
  },
  '& input': {
    '&::placeholder': {
      color: mode === 'dark' ? '#b7c4d6' : '#526174',
      opacity: 1,
    },
    '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus, &:-webkit-autofill:active': {
      WebkitBoxShadow: mode === 'dark' ? '0 0 0 30px #131c33 inset !important' : '0 0 0 30px #ffffff inset !important',
      WebkitTextFillColor: mode === 'dark' ? '#ffffff !important' : '#000000 !important',
      transition: 'background-color 5000s ease-in-out 0s',
      borderRadius: 0,
    }
  },
  '& .MuiInputLabel-root': {
    fontWeight: 600,
    color: mode === 'dark' ? '#b7c4d6' : '#506177',
  }
}));

const StyledSelect = styled(Select)<{ mode?: string }>(({ theme, mode }: any) => ({
  minHeight: 58,
  backgroundColor: mode === 'dark' ? '#0b172a' : '#ffffff',
  borderRadius: 8,
  transition: 'border-color 160ms ease-out, box-shadow 160ms ease-out',
  '& fieldset': { borderColor: mode === 'dark' ? '#52627a' : '#aeb8c7' },
  '&:hover fieldset': { borderColor: mode === 'dark' ? '#8fa1b8' : '#65758b' },
  '&.Mui-focused': {
    boxShadow: `0 0 0 3px ${theme.palette.primary.main}24`,
  }
}));

const evidenceStages = [
  { title: 'Email captured', date: '29 AUG 2026', time: '09:14' },
  { title: 'Lead reconciled', date: '29 AUG 2026', time: '09:27' },
  { title: 'Partial bid approved', date: '29 AUG 2026', time: '11:03' },
  { title: 'RFQ promoted', date: '29 AUG 2026', time: '14:42' },
  { title: 'Order fulfilled', date: '29 AUG 2026', time: '16:18' },
  { title: 'Payment posted', date: '30 AUG 2026', time: '10:31' },
];

const ledgerRows = [
  ['Status', 'Complete', 'Complete', 'Complete', 'Complete', 'Complete', 'Complete'],
  ['Source document', 'Email', 'CRM Lead', 'BID-2026-0017 (Partial)', 'RFQ-2026-0042', 'SO-2026-0156', 'INV-2026-0312 / PAY-2026-0289'],
  ['Reference', 'MSG-87321', 'LEAD-009871', 'BID-2026-0017-P1', 'RFQ-2026-0042', 'SO-2026-0156', 'INV-2026-0312 / PAY-2026-0289'],
  ['Approved lines', '1 of 12', '1 of 12', '3 of 12', '6 of 12', '8 of 12', '8 of 12'],
  ['Amount (USD)', '—', '—', '38,250.00', '76,500.00', '128,750.00', '128,750.00'],
  ['By', 'System', 'Sarah Mitchell', 'Daniel Archer', 'Daniel Archer', 'Operations bot', 'Finance bot'],
  ['Notes', 'Inbound email captured', 'Account matched and validated', 'Partial bid approved', 'RFQ created from bid', 'Order fulfilled and goods shipped', 'Payment received and applied'],
];

// --- Auth service typing ---
interface LoginBusinessUnitOption {
  id: number;
  name: string;
}

interface LoginRequest {
  email: string;
  password: string;
  /** Only sent when disambiguating an email that exists in multiple organizations. */
  businessUnitId?: number;
}

interface LoginResponse {
  id: number;
  email: string;
  userName: string;
  roleId: number | null;
  roleName: string;
  isSuperAdmin: boolean;
  isManager: boolean;
  businessUnitId: number | null;
  businessUnitName: string | null;
  token: string;
  /** Set (with businessUnits, and no token) when the client must pick an organization. */
  requiresBusinessUnitSelection?: boolean;
  businessUnits?: LoginBusinessUnitOption[];
}

/**
 * Where a user lands after signing in.
 *
 * Everyone lands on the Inbox — the one screen that answers "what do I do next" instead of asking
 * the reader to choose a module.
 *
 * The history is worth keeping, because it is the reason this is a constant now. Login first called
 * `navigate('/analytics/deadlines')` unconditionally; that route is gated on Leads, and of the four
 * seeded starter roles only SALES_MANAGER and SALES_REP hold it, so a Procurement Officer signed in
 * successfully and landed on "Access Denied" as their first screen. The fix at the time was a
 * per-permission fallback table — seven candidate destinations, each with its own module gate. That
 * worked, but it meant four roles saw four different first screens, and none of the seven could show
 * work that had not yet become a lead.
 *
 * `/inbox` is deliberately UNGUARDED at the route and asks for each queue separately, so the
 * permission problem cannot recur: a user with no grants at all gets a page that names what is
 * missing and points at Setup, which is the correct answer to "I have no access", and a user with
 * some grants gets exactly the queues they can work. One first screen, for everybody.
 */
export const LANDING_ROUTE = INBOX_ROOT;

/**
 * Kept as a function, and still exported, because the sign-in flow and its tests both call it and
 * because the shape leaves room for a future per-role landing without moving the call site. It
 * ignores its arguments by design — see above: branching on permissions here is what produced the
 * Access Denied first screen.
 */
export const landingRouteFor = (
  _isSuperAdmin: boolean,
  _permissions: ReadonlyArray<{ moduleName: string; canView?: boolean }>,
): string => LANDING_ROUTE;

const LoginPage: React.FC = () => {
  const { mode, setMode } = useAppTheme();
  const { setToken, setUserData } = useAuth();
  const navigate = useNavigate();

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);

  // FE-12: show a friendly message when we were redirected here because the
  // session expired (set by AuthContext), then clear it so it shows only once.
  useEffect(() => {
    const message = sessionStorage.getItem('authNotice');
    if (message) {
      setNotice(message);
      sessionStorage.removeItem('authNotice');
    }
  }, []);
  
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  // Rare case: the same email is valid in multiple organizations and the
  // server asks which one to sign in to.
  const [businessUnitOptions, setBusinessUnitOptions] = useState<LoginBusinessUnitOption[] | null>(null);
  const [selectedBusinessUnitId, setSelectedBusinessUnitId] = useState<number | ''>('');

  const submitLogin = async (businessUnitId?: number) => {
    setLoading(true);
    setError(null);
    try {
      const payload: LoginRequest = { email, password };
      if (businessUnitId !== undefined) {
        payload.businessUnitId = businessUnitId;
      }
      const response = await axiosInstance.post<LoginResponse>("/api/Auth/Login", payload);
      const data = response.data;

      if (data.requiresBusinessUnitSelection) {
        // No token yet — show the organization chooser and retry with the pick.
        setBusinessUnitOptions(data.businessUnits ?? []);
        setSelectedBusinessUnitId('');
        return;
      }

      setToken(data.token);

      // Bootstrap the caller's own grants.
      //
      // This used to read the whole role-permission table through an endpoint gated on
      // "Roles & Permissions: View". Any role without that specific module got a 403, the failure
      // was logged to the console and swallowed, and the user was navigated into an app with zero
      // permissions — empty sidebar, Access Denied everywhere, and nothing on screen explaining
      // why. `/api/User/me/permissions` needs authentication only, and a failure now BLOCKS the
      // login instead of producing a silently crippled session.
      let me: MePermissionsResponse;
      try {
        me = await userService.getMyPermissions();
      } catch (permErr) {
        // Roll the half-established session back so the user lands on a clean login screen
        // rather than a shell they cannot use.
        setToken(null);
        setError(
          presentableErrorMessage(
            permErr,
            'Signed in, but your permissions could not be loaded. Contact your administrator.',
          ),
        );
        return;
      }

      setUserData({
        id: me.userId ?? data.id,
        email: data.email,
        userName: data.userName,
        roleName: me.roleName ?? data.roleName,
        // Authority comes from the server's own RoleGate, not from a second derivation on the
        // login response — the two could disagree, rendering a UI every API call then rejects.
        isSuperAdmin: me.isSuperAdmin === true,
        isManager: me.isManager === true,
        hasModuleAuthorityByRank: me.hasModuleAuthorityByRank === true,
        roleId: me.roleId ?? data.roleId ?? undefined,
        businessUnitId: me.businessUnitId ?? data.businessUnitId ?? undefined,
        permissions: me.permissions ?? [],
        entitlements: me.entitlements ?? [],
      });
      navigate(landingRouteFor(me.isSuperAdmin === true, me.permissions ?? []));
    } catch (err: any) {
      setError(err.response?.data?.error || err.response?.data?.message || "Invalid credentials");
    } finally {
      setLoading(false);
    }
  };

  const handleLogin = (e: React.FormEvent) => {
    e.preventDefault();
    if (businessUnitOptions) {
      if (selectedBusinessUnitId === '') return;
      submitLogin(selectedBusinessUnitId);
    } else {
      submitLogin();
    }
  };

  const resetBusinessUnitSelection = () => {
    setBusinessUnitOptions(null);
    setSelectedBusinessUnitId('');
    setError(null);
  };

  return (
    <Container mode={mode} data-testid="login-viewport">
      <LoginShell data-testid="login-card">
        <EvidencePanel aria-label="Nexora evidence-to-cash workflow">
          <Box
            sx={{
              '& .MuiTypography-root': { color: '#f8fafc !important' },
              '& img': { filter: 'brightness(0) invert(1)' },
              mb: { xs: 1.5, md: 3 },
            }}
          >
            <Branding fontSize={28} logoSize={38} />
          </Box>

          <Typography
            component="h2"
            sx={{
              maxWidth: 720,
              fontFamily: '"Cambay", "Source Sans 3", sans-serif',
              fontSize: { xs: 29, sm: 48, lg: 56 },
              fontWeight: 700,
              lineHeight: 1.08,
              letterSpacing: '-0.025em',
              color: '#f8fafc',
              mb: { xs: .5, md: 3.5 },
            }}
          >
            Every commercial decision, connected to its evidence.
          </Typography>

          <Box sx={{ display: { xs: 'none', lg: 'block' } }}>
            <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 2, mb: 2 }}>
              <Typography sx={{ color: '#9fb0c8', fontSize: 14, letterSpacing: '0.13em', textTransform: 'uppercase' }}>
                Commercial chain-of-custody command ledger
              </Typography>
              <Typography sx={{ flexShrink: 0, color: '#b8c9de', fontSize: 12, fontWeight: 700, letterSpacing: '.08em', textTransform: 'uppercase' }}>
                Illustrative record
              </Typography>
            </Box>
            <Box
              aria-label="Illustrative commercial record"
              sx={{
                py: 1.25,
                borderTop: '1px solid #283b58',
                display: 'grid',
                gridTemplateColumns: '1fr 1.3fr .8fr 1.1fr 1fr',
                gap: 2,
              }}
            >
              {[
                ['Enquiry ID', 'ENQ-2026-DEMO'],
                ['Account', 'Northbridge Logistics Ltd'],
                ['Value (USD)', '128,750.00'],
                ['Created', '29 Aug 2026 · 09:14'],
                ['Owner', 'Sarah Mitchell'],
              ].map(([label, value]) => (
                <Box key={label}>
                  <Typography sx={{ color: '#9fb0c8', fontSize: 11, textTransform: 'uppercase', letterSpacing: '.06em' }}>{label}</Typography>
                  <Typography
                    className="tabular-nums"
                    sx={{
                      color: '#f8fafc',
                      fontSize: 16,
                      fontWeight: 600,
                      mt: .5,
                    }}
                  >
                    {value}
                  </Typography>
                </Box>
              ))}
            </Box>
          </Box>

          <Typography
            sx={{
              display: { xs: 'block', lg: 'none' },
              color: '#b8c9de',
              fontSize: 10,
              fontWeight: 700,
              letterSpacing: '.1em',
              textAlign: 'center',
              textTransform: 'uppercase',
              mb: .5,
            }}
          >
            Illustrative workflow · demonstration only
          </Typography>

          <Box
            component="ol"
            aria-label="Governed commercial stages"
            sx={{
              listStyle: 'none',
              p: 0,
              m: { xs: 'auto 0 0', lg: '8px 0 0' },
              display: 'grid',
              gridTemplateColumns: { xs: 'repeat(3, 1fr)', sm: 'repeat(6, 1fr)' },
              border: { xs: 0, lg: '1px solid #35506f' },
              borderBottom: { xs: '1px solid #283b58', lg: '1px solid #35506f' },
            }}
          >
            {evidenceStages.map((stage, index) => (
              <Box
                component="li"
                key={stage.title}
                sx={{
                  minWidth: 0,
                  px: { xs: .5, lg: 1.5 },
                  py: { xs: .5, lg: 3.75 },
                  textAlign: 'center',
                  borderRight: index < evidenceStages.length - 1 ? { xs: 0, lg: '1px solid #35506f' } : 0,
                  position: 'relative',
                }}
              >
                <Typography sx={{ color: '#f8fafc', fontSize: { xs: 10, sm: 11, lg: 13 }, fontWeight: 400, letterSpacing: '.04em', lineHeight: 1.15, minHeight: { lg: 30 }, maxWidth: { lg: 86 }, mx: 'auto' }}>
                  {stage.title}
                </Typography>
                <Typography className="tabular-nums" sx={{ display: { xs: 'none', sm: 'block' }, color: '#9fb0c8', fontSize: 10, mt: .5 }}>
                  {stage.date}<br />{stage.time}
                </Typography>
                <Box sx={{ display: { xs: 'none', lg: 'flex' }, alignItems: 'center', my: 1, '&::before, &::after': { content: '""', height: 2, flex: 1, background: '#20c7b5' } }}>
                  <Box sx={{ width: 46, height: 46, mx: -.5, border: '2px solid #20c7b5', borderRadius: '50%', display: 'grid', placeItems: 'center', background: '#08172a' }}>
                    <CheckIcon aria-hidden="true" sx={{ color: '#f8fafc', fontSize: 23 }} />
                  </Box>
                </Box>
                <CheckIcon aria-hidden="true" sx={{ display: { xs: 'inline-flex', lg: 'none' }, color: '#20c7b5', fontSize: 17, mt: .5 }} />
              </Box>
            ))}
          </Box>

          <Box sx={{ display: { xs: 'none', lg: 'block' }, mt: 0 }}>
            <Box component="table" sx={{ width: '100%', borderCollapse: 'collapse', tableLayout: 'fixed' }}>
              <Box component="caption" sx={visuallyHidden}>
                Illustrative commercial chain-of-custody ledger from captured email through payment.
              </Box>
              <Box component="thead" sx={visuallyHidden}>
                <Box component="tr">
                  <th id="ledger-attribute" scope="col">Attribute</th>
                  {evidenceStages.map((stage, index) => (
                    <th id={`ledger-stage-${index}`} scope="col" key={stage.title}>{stage.title}</th>
                  ))}
                </Box>
              </Box>
              <Box component="tbody">
                {ledgerRows.map((row) => {
                  const rowHeaderId = `ledger-row-${row[0].toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;

                  return (
                    <Box component="tr" key={row[0]}>
                      {row.map((cell, index) => {
                        const cellStyle = {
                          padding: '12px 10px',
                          border: '1px solid #35506f',
                          color: index === 0 ? '#b2c4dc' : row[0] === 'Status' || (row[0] === 'Approved lines' && index === row.length - 1) || (row[0] === 'Amount (USD)' && index === row.length - 1) ? '#20c7b5' : '#e1e9f3',
                          fontSize: 11.5,
                          fontWeight: index === 0 ? 600 : 400,
                          textAlign: 'left' as const,
                          overflowWrap: 'anywhere' as const,
                        };

                        return index === 0 ? (
                          <th
                            scope="row"
                            id={rowHeaderId}
                            key={`${row[0]}-${index}`}
                            style={cellStyle}
                          >
                            {cell}
                          </th>
                        ) : (
                          <td
                            headers={`${rowHeaderId} ledger-stage-${index - 1}`}
                            key={`${row[0]}-${index}`}
                            style={cellStyle}
                          >
                            {cell}
                          </td>
                        );
                      })}
                    </Box>
                  );
                })}
              </Box>
            </Box>
          </Box>

          <Box
            sx={{
              display: { xs: 'none', lg: 'flex' },
              alignItems: 'center',
              gap: 1.5,
              mt: 'auto',
              px: 2,
              py: 1.75,
              border: '1px solid #35506f',
              borderRadius: '4px',
              color: '#dce5f0',
            }}
          >
            <IntegrityIcon aria-hidden="true" sx={{ color: '#8fa6c3', fontSize: 38 }} />
            <Box>
              <Typography sx={{ fontSize: 12, fontWeight: 700, letterSpacing: '.06em', textTransform: 'uppercase' }}>Ledger integrity</Typography>
              <Typography sx={{ color: '#9fb0c8', fontSize: 12 }}>All events are tamper-evident and time-stamped (UTC).</Typography>
            </Box>
            <Typography sx={{ ml: 'auto', color: '#b8c9de', fontSize: 12, fontWeight: 700, letterSpacing: '.08em', textTransform: 'uppercase' }}>
              Demonstration only
            </Typography>
          </Box>
        </EvidencePanel>

        <FormSection mode={mode} id={MAIN_CONTENT_ID} tabIndex={-1}>
          <Box sx={{ position: 'absolute', top: { xs: 14, sm: 24 }, right: { xs: 14, sm: 24 } }}>
            <IconButton
              onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')}
              aria-label={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
              sx={{ width: 50, height: 50, border: '1px solid', borderColor: mode === 'dark' ? '#52627a' : '#c7ced8', borderRadius: '7px' }}
            >
              {mode === 'dark' ? <SunIcon /> : <MoonIcon />}
            </IconButton>
          </Box>

          <Box sx={{ maxWidth: 480, width: '100%', mx: 'auto', pt: { xs: 4, sm: 0 } }}>
            <Typography
              component="h1"
              sx={{
                fontFamily: '"Cambay", "Source Sans 3", sans-serif',
                fontSize: { xs: 36, md: 56 },
                fontWeight: 700,
                lineHeight: 1.1,
                letterSpacing: '-0.025em',
                mb: 1.5,
              }}
            >
              Sign in to Nexora
            </Typography>
            <Typography variant="body1" sx={{ color: mode === 'dark' ? '#b7c4d6' : '#526174', mb: { xs: 3, sm: 6 }, lineHeight: 1.55, maxWidth: 440, fontSize: { sm: 18 } }}>
              Access your procurement and order-to-cash workspace with complete chain-of-custody visibility.
            </Typography>

            <Box component="form" onSubmit={handleLogin}>
              {notice && <Alert role="status" severity="info" sx={{ mb: 3, borderRadius: 2 }}>{notice}</Alert>}
              {businessUnitOptions ? (
                <>
                  <Alert severity="info" sx={{ mb: 3, borderRadius: 2 }}>
                    Your account belongs to more than one organization. Pick one to continue.
                  </Alert>
                  <FormControl fullWidth sx={{ mb: 4 }}>
                    <InputLabel id="bu-label">Which organization?</InputLabel>
                    <StyledSelect
                      mode={mode}
                      id="business-unit"
                      name="businessUnitId"
                      labelId="bu-label"
                      label="Which organization?"
                      value={selectedBusinessUnitId}
                      onChange={(e) => setSelectedBusinessUnitId(Number(e.target.value))}
                      required
                      error={Boolean(error)}
                      aria-describedby={error ? LOGIN_ERROR_ID : undefined}
                    >
                      {businessUnitOptions.map((bu) => (
                        <MenuItem key={bu.id} value={bu.id}>
                          {bu.name}
                        </MenuItem>
                      ))}
                    </StyledSelect>
                  </FormControl>
                </>
              ) : (
                <>
                  <StyledTextField
                    mode={mode}
                    fullWidth
                    id="email"
                    name="email"
                    // type=email gets the right mobile keyboard and native
                    // validation; autoComplete lets password managers and
                    // browser autofill work (SC 1.3.5 Identify Input Purpose).
                    type="email"
                    placeholder="you@example.com"
                    autoComplete="username"
                    inputMode="email"
                    label="Email address"
                    variant="outlined"
                    sx={{ mb: 3 }}
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                    error={Boolean(error)}
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <MailIcon sx={{ color: 'text.secondary' }} />
                          </InputAdornment>
                        ),
                      },
                      htmlInput: {
                        inputMode: 'email',
                        'aria-invalid': Boolean(error),
                        'aria-describedby': error ? LOGIN_ERROR_ID : undefined,
                      },
                    }}
                  />

                  <StyledTextField
                    mode={mode}
                    fullWidth
                    id="password"
                    name="password"
                    autoComplete="current-password"
                    label="Password"
                    type={showPassword ? 'text' : 'password'}
                    variant="outlined"
                    sx={{ mb: 4 }}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                    error={Boolean(error)}
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <LockIcon sx={{ color: 'text.secondary' }} />
                          </InputAdornment>
                        ),
                        endAdornment: (
                          <InputAdornment position="end">
                            <IconButton
                              onClick={() => setShowPassword(!showPassword)}
                              edge="end"
                              // Icon-only control: needs a name, and the toggle
                              // state has to be exposed, not just implied by
                              // which icon is drawn (SC 4.1.2).
                              aria-label={showPassword ? 'Hide password' : 'Show password'}
                              aria-pressed={showPassword}
                              sx={{ width: 44, height: 44 }}
                            >
                              {showPassword ? <VisibilityOff /> : <Visibility />}
                            </IconButton>
                          </InputAdornment>
                        ),
                      },
                      htmlInput: {
                        'aria-invalid': Boolean(error),
                        'aria-describedby': error ? LOGIN_ERROR_ID : undefined,
                      },
                    }}
                  />

                  {/* The way out for somebody who cannot get in.
                      Rendered only on the credentials step: the business-unit
                      selection below appears AFTER the password has already been
                      verified, and offering "forgot password" to someone who
                      just proved they remember it is noise.
                      Until this page had a link, the only recovery route was for
                      an operator to overwrite the hash in the database — and
                      ActivateAccountPage has been telling users to "use forgot
                      password on the sign-in page" the whole time. */}
                  <Box sx={{ mt: -3, mb: 3, display: 'flex', justifyContent: 'flex-end' }}>
                    <Button
                      component={Link}
                      to="/forgot-password"
                      variant="text"
                      size="small"
                      sx={{ color: mode === 'dark' ? '#79b7ff' : '#075dcc', fontWeight: 600, fontSize: 16, textTransform: 'none' }}
                    >
                      Forgot password?
                    </Button>
                  </Box>
                </>
              )}

              {/* id lets the fields point at this message via
                  aria-describedby; role="alert" announces it on failure. */}
              {error && (
                <Alert id={LOGIN_ERROR_ID} role="alert" severity="error" sx={{ mb: 3, borderRadius: 2 }}>
                  {error}
                </Alert>
              )}

              <Button
                fullWidth
                variant="contained"
                size="large"
                type="submit"
                disabled={loading || (businessUnitOptions !== null && selectedBusinessUnitId === '')}
                aria-busy={loading}
                sx={{
                  minHeight: 61,
                  py: 1.5,
                  fontSize: 16,
                  borderRadius: '8px',
                  background: '#075dcc',
                  color: '#ffffff',
                  boxShadow: '0 10px 24px -16px rgba(9, 105, 232, .8)',
                  transition: 'background-color 160ms ease-out, box-shadow 160ms ease-out',
                  '&:hover': { background: '#064da9', boxShadow: '0 12px 26px -16px rgba(9, 105, 232, .9)' },
                  '@media (min-width: 1101px)': { mt: 4 },
                }}
              >
                {loading ? (
                  <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1.25 }}>
                    <CircularProgress size={20} color="inherit" aria-hidden="true" />
                    Signing in…
                  </Box>
                ) : businessUnitOptions ? 'Continue' : 'Sign in'}
              </Button>

              {businessUnitOptions && (
                <Button
                  fullWidth
                  variant="text"
                  onClick={resetBusinessUnitSelection}
                  disabled={loading}
                  sx={{ mt: 2 }}
                >
                  Back to sign in
                </Button>
              )}
            </Box>

            {!businessUnitOptions && (
              <Box
                component="aside"
                aria-label="Platform administration sign-in"
                sx={{
                  mt: { xs: 2, sm: 7 },
                  pt: { xs: 2, sm: 2.5 },
                  borderTop: '1px solid',
                  borderColor: 'divider',
                  textAlign: 'center',
                }}
              >
                <Button
                  component={Link}
                  to="/platform/tenants"
                  variant="text"
                  size="small"
                  startIcon={<SettingsIcon aria-hidden="true" />}
                  sx={{ minHeight: 44, color: mode === 'dark' ? '#79b7ff' : '#075dcc', fontSize: 17, fontWeight: 600, textTransform: 'none' }}
                >
                  Platform administration
                </Button>
              </Box>
            )}
          </Box>
        </FormSection>
      </LoginShell>
    </Container>
  );
};

export default LoginPage;
