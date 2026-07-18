import React, { useEffect, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  FormControl,
  GlobalStyles,
  IconButton,
  InputAdornment,
  InputLabel,
  MenuItem,
  Select,
  TextField,
  Typography,
} from '@mui/material';
import {
  MailOutlined as MailIcon,
  LockOutlined as LockIcon,
  Visibility,
  VisibilityOff,
} from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import axiosInstance from '../../api/axiosInstance';
import rolePermissionService from '../../api/services/rolePermissionService';
import logo from '../../assets/img/logo.svg';
import AuroraBackdrop from './components/AuroraBackdrop';
import BentoTiles from './components/BentoTiles';
import Spotlight from './components/Spotlight';
import {
  ACCENT,
  ACCENT_DEEP,
  ACCENT_SOFT,
  ACCENT_TINT,
  HAIRLINE,
  PAGE_BG,
  TEXT_HI,
  TEXT_LOW,
  TEXT_MID,
  errorAlertSx,
  focusRingSx,
  glassFieldSx,
  glassSelectSx,
  glassSx,
  infoAlertSx,
} from './components/tokens';

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
  businessUnitId: number | null;
  businessUnitName: string | null;
  token: string;
  /** Set (with businessUnits, and no token) when the client must pick an organization. */
  requiresBusinessUnitSelection?: boolean;
  businessUnits?: LoginBusinessUnitOption[];
}

const LoginPage: React.FC = () => {
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

      // Fetch permissions for the logged in role
      let permissions: any[] = [];
      if (data.roleId != null && data.businessUnitId != null) {
        try {
          permissions = await rolePermissionService.getPermissionsByRole(data.roleId, data.businessUnitId);
        } catch (permErr) {
          console.error("Failed to fetch permissions", permErr);
        }
      }

      setUserData({
        id: data.id,
        email: data.email,
        userName: data.userName,
        roleName: data.roleName,
        roleId: data.roleId ?? undefined,
        businessUnitId: data.businessUnitId ?? undefined,
        permissions: permissions,
      });
      navigate('/dashboard');
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
    <Box
      component="main"
      sx={{
        // 100vh first for browsers without dvh (Safari < 15.4), then the
        // iOS-correct dynamic viewport unit where supported.
        minHeight: '100vh',
        '@supports (min-height: 100dvh)': { minHeight: '100dvh' },
        position: 'relative',
        backgroundColor: PAGE_BG,
        color: TEXT_HI,
        fontFamily: '"Outfit", "Inter", sans-serif',
        '& *::selection': {
          backgroundColor: 'rgba(70, 130, 180, 0.55)',
          color: '#FFFFFF',
          WebkitTextFillColor: '#FFFFFF',
        },
      }}
    >
      {/* While this pre-auth stage is mounted, force dark UA chrome (document
          scrollbar, native control tints) regardless of the in-app theme mode.
          Unmounts (and reverts) on navigation. */}
      <GlobalStyles
        styles={{
          ':root': { colorScheme: 'dark' },
          body: { scrollbarColor: '#2E4260 #0A101C' },
          'body::-webkit-scrollbar-track, body *::-webkit-scrollbar-track': {
            backgroundColor: '#0A101C',
          },
          'body::-webkit-scrollbar-thumb, body *::-webkit-scrollbar-thumb': {
            backgroundColor: '#2E4260',
            borderColor: '#0A101C',
          },
          'body::-webkit-scrollbar-thumb:hover, body *::-webkit-scrollbar-thumb:hover': {
            backgroundColor: '#3D5678',
          },
        }}
      />
      <AuroraBackdrop />
      <Spotlight />

      <Box
        sx={{
          position: 'relative',
          zIndex: 1,
          minHeight: '100vh',
          '@supports (min-height: 100dvh)': { minHeight: '100dvh' },
          display: 'flex',
          flexDirection: 'column',
          maxWidth: 1280,
          mx: 'auto',
          px: { xs: 2.5, sm: 4, md: 6 },
          py: { xs: 4, md: 5 },
        }}
      >
        <Box
          sx={{
            flex: 1,
            display: 'grid',
            alignContent: 'center',
            columnGap: { lg: 8 },
            rowGap: { xs: 4, lg: 4.5 },
            gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1fr) minmax(380px, 440px)' },
            gridTemplateAreas: {
              xs: '"intro" "card" "tiles"',
              lg: '"intro card" "tiles card"',
            },
          }}
        >
          {/* --- Landing pitch: one confident headline --- */}
          <Box sx={{ gridArea: 'intro' }}>
            <Typography
              component="h1"
              sx={{
                fontWeight: 800,
                letterSpacing: '-0.03em',
                lineHeight: 1.05,
                color: TEXT_HI,
                fontSize: { xs: '2.25rem', sm: '2.9rem', lg: '3.4rem' },
                mb: 2,
              }}
            >
              Sourcing, run by{' '}
              <Box
                component="span"
                sx={{
                  background: `linear-gradient(100deg, ${ACCENT_SOFT} 0%, ${ACCENT_TINT} 100%)`,
                  WebkitBackgroundClip: 'text',
                  backgroundClip: 'text',
                  color: 'transparent',
                  '@supports not (-webkit-background-clip: text)': {
                    background: 'none',
                    color: ACCENT_SOFT,
                  },
                }}
              >
                intelligence.
              </Box>
            </Typography>
            <Typography sx={{ color: TEXT_MID, fontSize: { xs: 15, sm: 16.5 }, lineHeight: 1.6, maxWidth: 560 }}>
              Nexora reads your RFQs, sources suppliers, and builds quotes — then routes every
              decision through your team before anything moves.
            </Typography>
          </Box>

          {/* --- The glass login card --- */}
          <Box
            component="section"
            aria-label="Sign in to Nexora"
            sx={{
              gridArea: 'card',
              alignSelf: 'center',
              justifySelf: { xs: 'center', lg: 'end' },
              width: '100%',
              maxWidth: 440,
              ...glassSx,
              borderRadius: '28px',
              p: { xs: 3, sm: 4 },
              boxShadow: '0 30px 90px -30px rgba(0, 0, 0, 0.8), 0 0 70px -25px rgba(70, 130, 180, 0.35)',
            }}
          >
            {/* Wordmark */}
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, mb: 4 }}>
              <Box
                sx={{
                  width: 44,
                  height: 44,
                  borderRadius: '12px',
                  flexShrink: 0,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  background: `linear-gradient(135deg, ${ACCENT} 0%, ${ACCENT_DEEP} 100%)`,
                  boxShadow: '0 10px 26px -10px rgba(70, 130, 180, 0.6)',
                }}
              >
                <img src={logo} alt="" height={24} style={{ filter: 'brightness(0) invert(1)' }} />
              </Box>
              <Box>
                <Typography sx={{ fontSize: 22, fontWeight: 900, letterSpacing: '-0.5px', lineHeight: 1.15, color: TEXT_HI }}>
                  NEXORA
                </Typography>
                <Typography sx={{ fontSize: 10.5, fontWeight: 700, letterSpacing: '0.14em', textTransform: 'uppercase', color: ACCENT_SOFT }}>
                  The Intelligence Platform
                </Typography>
              </Box>
            </Box>

            <Typography component="h2" sx={{ fontSize: 24, fontWeight: 800, letterSpacing: '-0.02em', color: TEXT_HI, mb: 0.5 }}>
              Welcome back
            </Typography>
            <Typography sx={{ color: TEXT_MID, fontSize: 14.5, mb: 3.5 }}>
              Sign in to your workspace to continue.
            </Typography>

            {notice && <Alert severity="info" sx={infoAlertSx}>{notice}</Alert>}

            <form onSubmit={handleLogin}>
              {businessUnitOptions ? (
                <>
                  <Alert severity="info" sx={infoAlertSx}>
                    Your account belongs to more than one organization. Pick one to continue.
                  </Alert>
                  <FormControl fullWidth sx={{ mb: 4 }}>
                    <InputLabel
                      id="bu-label"
                      sx={{ color: 'rgba(255, 255, 255, 0.70)', '&.Mui-focused': { color: ACCENT_SOFT } }}
                    >
                      Which organization?
                    </InputLabel>
                    <Select
                      labelId="bu-label"
                      label="Which organization?"
                      autoFocus
                      value={selectedBusinessUnitId}
                      onChange={(e) => setSelectedBusinessUnitId(Number(e.target.value))}
                      required
                      sx={glassSelectSx}
                      MenuProps={{
                        slotProps: {
                          paper: {
                            sx: {
                              mt: 1,
                              borderRadius: '14px',
                              backgroundColor: '#101B30',
                              backgroundImage: 'none',
                              border: `1px solid ${HAIRLINE}`,
                              color: TEXT_HI,
                              boxShadow: '0 20px 50px -12px rgba(0, 0, 0, 0.7)',
                            },
                          },
                        },
                      }}
                    >
                      {businessUnitOptions.map((bu) => (
                        <MenuItem
                          key={bu.id}
                          value={bu.id}
                          sx={{
                            '&:hover': { backgroundColor: 'rgba(255, 255, 255, 0.06)' },
                            // MUI's default focusVisible tint is invisible on this
                            // dark paper — make keyboard focus clearly readable.
                            '&.Mui-focusVisible': { backgroundColor: 'rgba(255, 255, 255, 0.12)' },
                            '&.Mui-selected': { backgroundColor: 'rgba(70, 130, 180, 0.24)' },
                            '&.Mui-selected:hover': { backgroundColor: 'rgba(70, 130, 180, 0.32)' },
                            '&.Mui-selected.Mui-focusVisible': { backgroundColor: 'rgba(70, 130, 180, 0.36)' },
                          }}
                        >
                          {bu.name}
                        </MenuItem>
                      ))}
                    </Select>
                  </FormControl>
                </>
              ) : (
                <>
                  <TextField
                    fullWidth
                    label="Email Address"
                    type="email"
                    autoComplete="email"
                    autoFocus
                    variant="outlined"
                    sx={{ ...glassFieldSx, mb: 3 }}
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <MailIcon sx={{ color: ACCENT_SOFT, opacity: 0.85 }} />
                          </InputAdornment>
                        ),
                      },
                    }}
                  />

                  <TextField
                    fullWidth
                    label="Password"
                    type={showPassword ? 'text' : 'password'}
                    autoComplete="current-password"
                    variant="outlined"
                    sx={{ ...glassFieldSx, mb: 4 }}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <LockIcon sx={{ color: ACCENT_SOFT, opacity: 0.85 }} />
                          </InputAdornment>
                        ),
                        endAdornment: (
                          <InputAdornment position="end">
                            <IconButton
                              onClick={() => setShowPassword(!showPassword)}
                              edge="end"
                              aria-label={showPassword ? 'Hide password' : 'Show password'}
                              sx={{ color: 'rgba(255, 255, 255, 0.70)', ...focusRingSx }}
                            >
                              {showPassword ? <VisibilityOff /> : <Visibility />}
                            </IconButton>
                          </InputAdornment>
                        ),
                      },
                    }}
                  />
                </>
              )}

              {/* Announce sign-in errors to assistive tech as they appear */}
              <Box aria-live="polite">
                {error && <Alert severity="error" sx={errorAlertSx}>{error}</Alert>}
              </Box>

              <Button
                fullWidth
                variant="contained"
                size="large"
                type="submit"
                disabled={loading || (businessUnitOptions !== null && selectedBusinessUnitId === '')}
                sx={{
                  py: 1.6,
                  fontSize: 15.5,
                  fontWeight: 700,
                  borderRadius: '14px',
                  color: '#ffffff',
                  background: `linear-gradient(135deg, #3D74A6 0%, ${ACCENT_DEEP} 100%)`,
                  boxShadow: '0 14px 34px -12px rgba(70, 130, 180, 0.55)',
                  transition: 'transform 0.2s ease, box-shadow 0.2s ease',
                  '&:hover': {
                    background: `linear-gradient(135deg, #427CAC 0%, ${ACCENT_DEEP} 100%)`,
                    transform: 'translateY(-1px)',
                    boxShadow: '0 18px 40px -12px rgba(70, 130, 180, 0.65)',
                  },
                  '&.Mui-disabled': {
                    background: 'rgba(255, 255, 255, 0.12)',
                    color: 'rgba(255, 255, 255, 0.45)',
                  },
                  ...focusRingSx,
                  '@media (prefers-reduced-motion: reduce)': {
                    transition: 'none',
                    '&:hover': { transform: 'none' },
                  },
                }}
              >
                {loading
                  ? <CircularProgress size={24} color="inherit" />
                  : businessUnitOptions ? 'Continue' : 'Sign in'}
              </Button>

              {businessUnitOptions && (
                <Button
                  fullWidth
                  variant="text"
                  onClick={resetBusinessUnitSelection}
                  disabled={loading}
                  sx={{
                    mt: 2,
                    color: ACCENT_SOFT,
                    fontWeight: 600,
                    borderRadius: '12px',
                    '&:hover': { backgroundColor: 'rgba(255, 255, 255, 0.06)' },
                    ...focusRingSx,
                  }}
                >
                  Back to sign in
                </Button>
              )}
            </form>
          </Box>

          {/* --- Bento feature tiles ---
              After the card in DOM so mobile reading/announcement order matches
              the stacked visual order (intro → card → tiles); grid areas keep
              the desktop placement unchanged. No focusables inside. */}
          <Box sx={{ gridArea: 'tiles' }}>
            <BentoTiles />
          </Box>
        </Box>

        {/* --- Thin footer line --- */}
        <Box component="footer" sx={{ mt: { xs: 5, lg: 6 }, pt: 2.5, borderTop: '1px solid rgba(255, 255, 255, 0.08)' }}>
          <Typography sx={{ fontSize: 12.5, color: TEXT_LOW, letterSpacing: '0.02em' }}>
            © Nexora
          </Typography>
        </Box>
      </Box>
    </Box>
  );
};

export default LoginPage;
