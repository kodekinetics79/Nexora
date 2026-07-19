import React, { useEffect, useRef, useState } from 'react';
import type { AnimationEvent } from 'react';
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
  AutoAwesomeOutlined as SparkIcon,
  VerifiedUserOutlined as ShieldIcon,
} from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import axiosInstance from '../../api/axiosInstance';
import rolePermissionService from '../../api/services/rolePermissionService';
import logo from '../../assets/img/logo.svg';
import AuroraBackdrop from './components/AuroraBackdrop';
import BentoTiles from './components/BentoTiles';
import ParticleField from './components/ParticleField';
import Spotlight from './components/Spotlight';
import useDepthStage from './components/useDepthStage';
import {
  ACCENT,
  ACCENT_SOFT,
  HAIRLINE,
  NO_BACKDROP_FILTER,
  PAGE_BG,
  SOLID_CARD_BG,
  TEXT_HI,
  TEXT_LOW,
  TEXT_MID,
  errorAlertSx,
  focusRingSx,
  glassFieldSx,
  glassSelectSx,
  infoAlertSx,
} from './components/tokens';
import {
  EASE_OUT,
  EASE_SPRING,
  REDUCED_MOTION,
  alertIn,
  borderTrace,
  cardMove,
  cardShake,
  fadeIn,
  glowPulse,
  iconFlip,
  riseIn,
  rowsIn,
  sheenSweep,
  stageSweep,
  swapFade,
  swapIn,
  swapOut,
} from './components/motion';

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

  // ------------------------------------------------------------------
  // "First Light / Depth Stack" choreography (presentation only — every
  // auth decision above still keys off businessUnitOptions / loading).
  // ------------------------------------------------------------------

  const stageRef = useRef<HTMLElement | null>(null);
  const tiltRef = useRef<HTMLDivElement | null>(null);
  const emailInputRef = useRef<HTMLInputElement | null>(null);
  const orgSelectRef = useRef<HTMLInputElement | null>(null);

  // Single pointer system: parallax + card tilt + specular + spotlight vars.
  useDepthStage(stageRef, tiltRef);

  // One-shot stage light sweep; unmounted from the DOM after its 900ms run.
  const [sweep, setSweep] = useState(
    () => !window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  );

  // Autofocus the email field at 650ms (immediately under reduced motion).
  useEffect(() => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      emailInputRef.current?.focus();
      return;
    }
    const id = window.setTimeout(() => emailInputRef.current?.focus(), 650);
    return () => window.clearTimeout(id);
  }, []);

  // On error: move focus to the first invalid field (spec §4).
  useEffect(() => {
    if (!error) return;
    if (businessUnitOptions) orgSelectRef.current?.focus();
    else emailInputRef.current?.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [error]);

  // Shared-axis chooser swap: displayChooser mirrors businessUnitOptions with
  // a 100ms out phase (160ms out animation, 60ms overlap with the 200ms in).
  const chooserActive = businessUnitOptions !== null;
  const [displayChooser, setDisplayChooser] = useState(false);
  const [swapPhase, setSwapPhase] = useState<'idle' | 'out' | 'in'>('idle');
  const lastBusinessUnitsRef = useRef<LoginBusinessUnitOption[]>([]);
  useEffect(() => {
    if (businessUnitOptions) lastBusinessUnitsRef.current = businessUnitOptions;
  }, [businessUnitOptions]);
  useEffect(() => {
    if (chooserActive === displayChooser) return;
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      // Reduced motion: 150ms opacity-only crossfade, no height/translate.
      setDisplayChooser(chooserActive);
      setSwapPhase('in');
      const t = window.setTimeout(() => setSwapPhase('idle'), 160);
      return () => window.clearTimeout(t);
    }
    setSwapPhase('out');
    const t1 = window.setTimeout(() => {
      setDisplayChooser(chooserActive);
      setSwapPhase('in');
    }, 100);
    const t2 = window.setTimeout(() => setSwapPhase('idle'), 380);
    return () => {
      window.clearTimeout(t1);
      window.clearTimeout(t2);
    };
  }, [chooserActive, displayChooser]);

  const chooserOptions = businessUnitOptions ?? lastBusinessUnitsRef.current;

  return (
    <Box
      component="main"
      ref={stageRef}
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
          backgroundColor: 'rgba(37, 99, 235, 0.65)',
          color: '#FFFFFF',
          WebkitTextFillColor: '#FFFFFF',
        },
        // Reduced-motion entrance: the whole stage does one 250ms fade and
        // every per-element entrance below turns itself off.
        [REDUCED_MOTION]: {
          animation: `${fadeIn} 250ms ease-out both`,
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
      <ParticleField />
      <Spotlight />

      {/* One light sweep, 0–900ms, then removed from the DOM. */}
      {sweep && (
        <Box
          aria-hidden="true"
          onAnimationEnd={() => setSweep(false)}
          sx={{
            position: 'fixed',
            inset: '-10%',
            zIndex: 2,
            pointerEvents: 'none',
            background:
              'linear-gradient(120deg, transparent 38%, rgba(255, 255, 255, 0.08) 47%, rgba(220, 238, 255, 0.10) 50%, rgba(255, 255, 255, 0.08) 53%, transparent 62%)',
            animation: `${stageSweep} 900ms ${EASE_OUT} both`,
          }}
        />
      )}

      <Box
        sx={{
          position: 'relative',
          zIndex: 1,
          minHeight: '100vh',
          '@supports (min-height: 100dvh)': { minHeight: '100dvh' },
          display: 'flex',
          flexDirection: 'column',
          maxWidth: 1320,
          mx: 'auto',
          px: { xs: 2.5, sm: 4, md: 6 },
          pt: { xs: 3, md: 3.5 },
          pb: { xs: 2.5, md: 3 },
        }}
      >
        {/* --- Top bar: wordmark left, trust pill right --- */}
        <Box
          component="header"
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            mb: { xs: 4, lg: 2 },
            animation: `${fadeIn} 480ms ${EASE_OUT} both`,
            [REDUCED_MOTION]: { animation: 'none' },
          }}
        >
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
            <Box
              sx={{
                width: 38,
                height: 38,
                borderRadius: '11px',
                flexShrink: 0,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                background: 'linear-gradient(135deg, #38BDF8 0%, #2563EB 60%, #7C3AED 120%)',
                boxShadow: '0 8px 24px -8px rgba(56, 189, 248, 0.65)',
              }}
            >
              <img src={logo} alt="" height={20} style={{ filter: 'brightness(0) invert(1)' }} />
            </Box>
            <Box>
              <Typography sx={{ fontSize: 18, fontWeight: 900, letterSpacing: '-0.02em', lineHeight: 1.1, color: TEXT_HI }}>
                NEXORA
              </Typography>
              <Typography sx={{ fontSize: 9.5, fontWeight: 700, letterSpacing: '0.16em', textTransform: 'uppercase', color: ACCENT_SOFT }}>
                Intelligence Platform
              </Typography>
            </Box>
          </Box>

          <Box
            sx={{
              display: { xs: 'none', sm: 'inline-flex' },
              alignItems: 'center',
              gap: 0.75,
              px: 1.75,
              py: 0.75,
              borderRadius: '999px',
              border: '1px solid rgba(110, 231, 183, 0.30)',
              backgroundColor: 'rgba(52, 211, 153, 0.10)',
            }}
          >
            <ShieldIcon sx={{ fontSize: 15, color: '#6EE7B7' }} />
            <Typography sx={{ fontSize: 12, fontWeight: 600, letterSpacing: '0.02em', color: '#A7F3D0' }}>
              Human-approved AI
            </Typography>
          </Box>
        </Box>

        {/* --- Main stage --- */}
        <Box
          sx={{
            flex: 1,
            display: 'grid',
            alignContent: 'center',
            columnGap: { lg: 9 },
            rowGap: { xs: 4, lg: 3.5 },
            gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1fr) minmax(390px, 442px)' },
            gridTemplateAreas: {
              xs: '"intro" "card" "tiles"',
              lg: '"intro card" "tiles card"',
            },
          }}
        >
          {/* --- Landing pitch: eyebrow, display headline, subline --- */}
          <Box sx={{ gridArea: 'intro', alignSelf: 'end' }}>
            <Box
              sx={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 0.75,
                px: 1.75,
                py: 0.75,
                mb: 2.5,
                borderRadius: '999px',
                border: '1px solid rgba(56, 189, 248, 0.35)',
                backgroundColor: 'rgba(56, 189, 248, 0.10)',
                boxShadow: '0 0 24px -6px rgba(56, 189, 248, 0.35)',
                animation: `${riseIn} 480ms ${EASE_OUT} both`,
                animationDelay: { xs: '56ms', md: '80ms' },
                [REDUCED_MOTION]: { animation: 'none' },
              }}
            >
              <SparkIcon sx={{ fontSize: 15, color: ACCENT }} />
              <Typography sx={{ fontSize: 12, fontWeight: 700, letterSpacing: '0.12em', textTransform: 'uppercase', color: ACCENT_SOFT }}>
                AI-powered sourcing
              </Typography>
            </Box>

            <Typography
              component="h1"
              sx={{
                fontWeight: 800,
                letterSpacing: '-0.035em',
                lineHeight: 1.04,
                color: TEXT_HI,
                fontSize: { xs: '2.4rem', sm: '3.1rem', lg: '3.75rem' },
                mb: 2,
                textShadow: '0 2px 40px rgba(37, 99, 235, 0.35)',
                animation: `${riseIn} 480ms ${EASE_OUT} both`,
                animationDelay: { xs: '112ms', md: '160ms' },
                [REDUCED_MOTION]: { animation: 'none' },
              }}
            >
              Sourcing, run by{' '}
              <Box
                component="span"
                sx={{
                  display: 'block',
                  background: 'linear-gradient(95deg, #7DD3FC 0%, #38BDF8 40%, #A78BFA 90%)',
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
            <Typography
              sx={{
                color: TEXT_MID,
                fontSize: { xs: 15.5, sm: 17 },
                lineHeight: 1.6,
                maxWidth: 540,
                animation: `${riseIn} 480ms ${EASE_OUT} both`,
                animationDelay: { xs: '168ms', md: '240ms' },
                [REDUCED_MOTION]: { animation: 'none' },
              }}
            >
              Nexora reads your RFQs, sources suppliers, and builds quotes — then routes
              every decision through your team before anything moves.
            </Typography>
          </Box>

          {/* --- The glass login card ---
              Wrapper: perspective + parallax. Tilt frame: 1px gradient ring +
              rotateX/Y vars + Z-settle entrance. Inside: border-trace overlay,
              specular highlight, and the glass section itself. */}
          <Box
            sx={{
              gridArea: 'card',
              alignSelf: 'center',
              justifySelf: { xs: 'center', lg: 'end' },
              width: '100%',
              maxWidth: 442,
              perspective: '1200px',
              transform: 'translate3d(calc(var(--px, 0) * 5px), calc(var(--py, 0) * 3px), 0)',
            }}
          >
            <Box
              ref={tiltRef}
              style={{ willChange: 'transform' }}
              onAnimationEnd={(e: AnimationEvent<HTMLDivElement>) => {
                // Release the entrance layer promotion (spec §6); the pointer
                // system re-promotes only while the tilt is actively tracking.
                if (e.target === e.currentTarget) {
                  e.currentTarget.style.willChange = 'auto';
                }
              }}
              sx={{
                position: 'relative',
                borderRadius: '26px',
                p: '1px',
                background:
                  'linear-gradient(165deg, rgba(186, 230, 253, 0.75) 0%, rgba(255, 255, 255, 0.14) 30%, rgba(139, 92, 246, 0.32) 65%, rgba(56, 189, 248, 0.55) 100%)',
                boxShadow:
                  '0 40px 110px -30px rgba(2, 6, 20, 0.9), 0 0 100px -18px rgba(56, 189, 248, 0.45)',
                transform: 'rotateX(var(--tiltX, 0deg)) rotateY(var(--tiltY, 0deg))',
                animation: `${fadeIn} 300ms ${EASE_OUT} backwards, ${cardMove} 440ms ${EASE_SPRING} backwards`,
                animationDelay: { xs: '140ms, 140ms', md: '200ms, 200ms' },
                [REDUCED_MOTION]: { animation: 'none' },
              }}
            >
              {/* Border light: one ACCENT→CYAN trace, 700–1100ms, then gone. */}
              <Box
                aria-hidden="true"
                sx={{
                  position: 'absolute',
                  inset: 0,
                  borderRadius: '26px',
                  p: '1px',
                  pointerEvents: 'none',
                  background: `linear-gradient(90deg, transparent 20%, ${ACCENT} 45%, #22D3EE 55%, transparent 80%)`,
                  backgroundSize: '250% 100%',
                  WebkitMask: 'linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0)',
                  WebkitMaskComposite: 'xor',
                  mask: 'linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0)',
                  maskComposite: 'exclude',
                  opacity: 0,
                  animation: `${borderTrace} 400ms linear both`,
                  animationDelay: { xs: '490ms', md: '700ms' },
                  [REDUCED_MOTION]: { animation: 'none' },
                }}
              />
              <Box
                component="section"
                aria-label="Sign in to Nexora"
                sx={{
                  position: 'relative',
                  borderRadius: '25px',
                  p: { xs: 3, sm: 4.5 },
                  backgroundColor: 'rgba(9, 15, 34, 0.45)',
                  backgroundImage: `
                    linear-gradient(115deg, rgba(255, 255, 255, 0.07) 0%, transparent 42%),
                    radial-gradient(130% 70% at 50% 0%, rgba(56, 189, 248, 0.16) 0%, transparent 55%)
                  `,
                  backdropFilter: 'blur(28px) saturate(170%)',
                  WebkitBackdropFilter: 'blur(28px) saturate(170%)',
                  [NO_BACKDROP_FILTER]: { backgroundColor: SOLID_CARD_BG },
                  // Error shake lives here (not on the entrance-animated frame)
                  // so replays never re-run the entrance.
                  animation: error ? `${cardShake} 240ms ease` : 'none',
                  [REDUCED_MOTION]: { animation: 'none' },
                }}
              >
                {/* Specular highlight, positioned by --mx/--my from the tilt. */}
                <Box
                  aria-hidden="true"
                  sx={{
                    position: 'absolute',
                    inset: 0,
                    borderRadius: '25px',
                    pointerEvents: 'none',
                    mixBlendMode: 'overlay',
                    opacity: 0.13,
                    background:
                      'radial-gradient(300px circle at var(--mx, 50%) var(--my, 20%), rgba(255, 255, 255, 0.95) 0%, rgba(255, 255, 255, 0) 70%)',
                    display: { xs: 'none', lg: 'block' },
                    '.nx-degrade &': { display: 'none' },
                    [REDUCED_MOTION]: { display: 'none' },
                  }}
                />
                <Typography component="h2" sx={{ fontSize: 27, fontWeight: 800, letterSpacing: '-0.02em', color: TEXT_HI, mb: 0.5 }}>
                  Welcome back
                </Typography>
                <Typography sx={{ color: TEXT_MID, fontSize: 14.5, mb: 3.5 }}>
                  Sign in to your workspace to continue.
                </Typography>

                {notice && <Alert severity="info" sx={infoAlertSx}>{notice}</Alert>}

                <form onSubmit={handleLogin}>
                  {/* Shared-axis swap region: height animates via
                      grid-template-rows, content via translateY/opacity. */}
                  <Box
                    sx={{
                      display: 'grid',
                      gridTemplateRows: '1fr',
                      ...(swapPhase === 'in' && {
                        animation: `${rowsIn} 250ms ${EASE_OUT}`,
                        [REDUCED_MOTION]: { animation: 'none' },
                      }),
                    }}
                  >
                    <Box
                      sx={{
                        minWidth: 0,
                        minHeight: 0,
                        overflow: swapPhase === 'idle' ? 'visible' : 'hidden',
                        ...(swapPhase === 'out' && {
                          animation: `${swapOut} 160ms ease forwards`,
                          [REDUCED_MOTION]: { animation: 'none', opacity: 0 },
                        }),
                        ...(swapPhase === 'in' && {
                          animation: `${swapIn} 200ms ${EASE_OUT} both`,
                          [REDUCED_MOTION]: { animation: `${swapFade} 150ms ease both` },
                        }),
                      }}
                    >
                      {displayChooser ? (
                        <>
                          <Alert severity="info" sx={infoAlertSx}>
                            Your account belongs to more than one organization. Pick one to continue.
                          </Alert>
                          <FormControl fullWidth sx={{ mb: 4 }}>
                            <InputLabel
                              id="bu-label"
                              sx={{ color: 'rgba(226, 236, 255, 0.72)', '&.Mui-focused': { color: ACCENT_SOFT } }}
                            >
                              Which organization?
                            </InputLabel>
                            <Select
                              labelId="bu-label"
                              label="Which organization?"
                              autoFocus
                              inputRef={orgSelectRef}
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
                                      backgroundColor: '#101B36',
                                      backgroundImage: 'none',
                                      border: `1px solid ${HAIRLINE}`,
                                      color: TEXT_HI,
                                      boxShadow: '0 20px 50px -12px rgba(0, 0, 0, 0.7)',
                                    },
                                  },
                                },
                              }}
                            >
                              {chooserOptions.map((bu) => (
                                <MenuItem
                                  key={bu.id}
                                  value={bu.id}
                                  sx={{
                                    '&:hover': { backgroundColor: 'rgba(255, 255, 255, 0.06)' },
                                    // MUI's default focusVisible tint is invisible on this
                                    // dark paper — make keyboard focus clearly readable.
                                    '&.Mui-focusVisible': { backgroundColor: 'rgba(255, 255, 255, 0.12)' },
                                    '&.Mui-selected': { backgroundColor: 'rgba(56, 189, 248, 0.22)' },
                                    '&.Mui-selected:hover': { backgroundColor: 'rgba(56, 189, 248, 0.30)' },
                                    '&.Mui-selected.Mui-focusVisible': { backgroundColor: 'rgba(56, 189, 248, 0.34)' },
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
                            variant="outlined"
                            inputRef={emailInputRef}
                            sx={{ ...glassFieldSx, mb: 3 }}
                            value={email}
                            onChange={(e) => setEmail(e.target.value)}
                            required
                            slotProps={{
                              input: {
                                startAdornment: (
                                  <InputAdornment position="start">
                                    <MailIcon sx={{ color: ACCENT_SOFT, opacity: 0.9 }} />
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
                                    <LockIcon sx={{ color: ACCENT_SOFT, opacity: 0.9 }} />
                                  </InputAdornment>
                                ),
                                endAdornment: (
                                  <InputAdornment position="end">
                                    <IconButton
                                      onClick={() => setShowPassword(!showPassword)}
                                      edge="end"
                                      aria-label={showPassword ? 'Hide password' : 'Show password'}
                                      sx={{ color: 'rgba(226, 236, 255, 0.72)', ...focusRingSx }}
                                    >
                                      {/* Keyed remount drives the 160ms rotateY
                                          + 120ms cross-fade on the icon only. */}
                                      <Box
                                        key={showPassword ? 'hide' : 'show'}
                                        component="span"
                                        sx={{
                                          display: 'inline-flex',
                                          animation: `${iconFlip} 160ms ease-out`,
                                          [REDUCED_MOTION]: { animation: 'none' },
                                        }}
                                      >
                                        {showPassword ? <VisibilityOff /> : <Visibility />}
                                      </Box>
                                    </IconButton>
                                  </InputAdornment>
                                ),
                              },
                            }}
                          />
                        </>
                      )}
                    </Box>
                  </Box>

                  {/* Announce sign-in errors to assistive tech as they appear */}
                  <Box aria-live="assertive">
                    {error && (
                      <Alert
                        severity="error"
                        sx={{
                          ...errorAlertSx,
                          animation: `${alertIn} 200ms ${EASE_OUT} both`,
                          [REDUCED_MOTION]: { animation: 'none' },
                        }}
                      >
                        {error}
                      </Alert>
                    )}
                  </Box>

                  <Button
                    fullWidth
                    variant="contained"
                    size="large"
                    type="submit"
                    disabled={loading || (businessUnitOptions !== null && selectedBusinessUnitId === '')}
                    sx={{
                      position: 'relative',
                      py: 1.6,
                      fontSize: 15.5,
                      fontWeight: 700,
                      letterSpacing: '0.01em',
                      borderRadius: '14px',
                      color: '#ffffff',
                      // Every gradient stop keeps white text ≥ 5:1 (AA).
                      background: 'linear-gradient(135deg, #2563EB 0%, #4F46E5 55%, #7C3AED 100%)',
                      boxShadow: 'inset 0 1px 0 rgba(255, 255, 255, 0.25)',
                      // Release is the 250ms spring; hover/press override below.
                      transition: `transform 250ms ${EASE_SPRING}, filter 180ms ease`,
                      // Ambient glow layer: pulses 0.55↔0.75 on a 5s cycle.
                      '&::after': {
                        content: '""',
                        position: 'absolute',
                        inset: 0,
                        borderRadius: 'inherit',
                        boxShadow: '0 16px 40px -12px rgba(56, 189, 248, 0.65)',
                        opacity: 0.65,
                        animation: `${glowPulse} 5s ease-in-out infinite`,
                        zIndex: -1,
                        pointerEvents: 'none',
                      },
                      '&:hover': {
                        background: 'linear-gradient(135deg, #2563EB 0%, #4F46E5 55%, #7C3AED 100%)',
                        filter: 'brightness(1.1) saturate(1.05)',
                        transform: 'translateY(-1px) scale(1.01)',
                        transition: `transform 180ms ${EASE_OUT}, filter 180ms ease`,
                        '&::after': { animation: 'none', opacity: 0.95 },
                      },
                      '&:active': {
                        transform: 'scale(0.985)',
                        transition: 'transform 90ms ease-out',
                      },
                      '&.Mui-disabled': loading
                        ? {
                            // Loading: keep the gradient, run a sheen sweep via
                            // background-position (size constant).
                            color: 'rgba(255, 255, 255, 0.9)',
                            backgroundImage:
                              'linear-gradient(100deg, transparent 35%, rgba(255, 255, 255, 0.22) 50%, transparent 65%), linear-gradient(135deg, #2563EB 0%, #4F46E5 55%, #7C3AED 100%)',
                            backgroundSize: '250% 100%, 100% 100%',
                            animation: `${sheenSweep} 1.2s linear infinite`,
                          }
                        : {
                            background: 'rgba(255, 255, 255, 0.12)',
                            color: 'rgba(255, 255, 255, 0.45)',
                            '&::after': { display: 'none' },
                          },
                      ...focusRingSx,
                      [REDUCED_MOTION]: {
                        transition: 'none',
                        '&:hover, &:active': { transform: 'none' },
                        '&::after': { animation: 'none' },
                        '&.Mui-disabled': { animation: 'none' },
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

                {/* Trust microcopy anchoring the card's bottom edge */}
                <Box
                  sx={{
                    mt: 3.5,
                    pt: 2.5,
                    borderTop: `1px solid ${HAIRLINE}`,
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    gap: 0.75,
                  }}
                >
                  <ShieldIcon sx={{ fontSize: 15, color: ACCENT_SOFT, opacity: 0.9 }} />
                  <Typography sx={{ fontSize: 12.5, color: TEXT_LOW, letterSpacing: '0.02em' }}>
                    Role-based access · Every session is audited
                  </Typography>
                </Box>
              </Box>
            </Box>
          </Box>

          {/* --- Bento feature tiles ---
              After the card in DOM so mobile reading/announcement order matches
              the stacked visual order (intro → card → tiles); grid areas keep
              the desktop placement unchanged. No focusables inside. */}
          <Box
            sx={{
              gridArea: 'tiles',
              alignSelf: 'start',
              transform: 'translate3d(calc(var(--px, 0) * 3px), calc(var(--py, 0) * 1.8px), 0)',
            }}
          >
            <BentoTiles />
          </Box>
        </Box>

        {/* --- Anchored footer line --- */}
        <Box
          component="footer"
          sx={{
            mt: { xs: 4, lg: 3.5 },
            pt: 2,
            borderTop: `1px solid ${HAIRLINE}`,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
            flexWrap: 'wrap',
            animation: `${fadeIn} 480ms ${EASE_OUT} both`,
            [REDUCED_MOTION]: { animation: 'none' },
          }}
        >
          <Typography sx={{ fontSize: 12.5, color: TEXT_LOW, letterSpacing: '0.02em' }}>
            © Nexora — procurement intelligence platform
          </Typography>
          <Typography sx={{ fontSize: 12.5, color: TEXT_LOW, letterSpacing: '0.02em' }}>
            Multi-tenant isolation · Role-based access · Full audit trail
          </Typography>
        </Box>
      </Box>
    </Box>
  );
};

export default LoginPage;
