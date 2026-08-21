import React, { useMemo } from 'react';
import { Outlet, Link as RouterLink, useLocation, useNavigate } from 'react-router-dom';
import { Autocomplete, Box, Breadcrumbs, Link, TextField, Typography } from '@mui/material';
import { NavigateNext as SeparatorIcon, TuneRounded as JumpIcon } from '@mui/icons-material';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../../context/AuthContext';
import { RequireAuth } from '../../components/common/PermissionGuard';
import {
  SETUP_ENTRIES,
  SETUP_ROOT,
  entryForLocation,
  groupOfEntry,
  setupEntryLabel,
  type SetupEntry,
} from './setupCatalog';

/**
 * Chrome shared by every screen under `/setup`.
 *
 * The sidebar no longer lists the individual settings, so this bar carries what the list used to:
 * where you are (breadcrumb) and how to get somewhere else in one move (the jump field). Both read
 * the catalogue, so a screen added there is reachable here without touching this file.
 *
 * The hub itself renders bare — it is the breadcrumb's root, and a breadcrumb pointing at the page
 * you are already on is furniture, not navigation.
 */
/**
 * Height the breadcrumb bar takes from the viewport, in pixels.
 *
 * Several setup screens size a grid with `calc(100vh - Npx)`. The bar sits above them, so it has to
 * be in that subtraction or the grid runs past the fold — exported rather than repeated so there is
 * one number to change if the bar ever changes shape.
 */
export const SETUP_CHROME_HEIGHT = 57;

const SetupShellChrome: React.FC = () => {
  const location = useLocation();
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const { t } = useTranslation();

  const isHub = location.pathname === SETUP_ROOT || location.pathname === `${SETUP_ROOT}/`;
  const current = entryForLocation(location.pathname, location.search);
  const currentGroup = current ? groupOfEntry(current.key) : undefined;

  const options = useMemo(
    () => SETUP_ENTRIES.filter((entry) => !entry.moduleName || hasPermission(entry.moduleName)),
    [hasPermission],
  );

  if (isHub) return <Outlet />;

  return (
    <Box>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 2,
          flexWrap: 'wrap',
          px: { xs: 2, md: 3 },
          py: 1,
          minHeight: SETUP_CHROME_HEIGHT,
          borderBottom: '1px solid',
          borderColor: 'divider',
        }}
      >
        <Breadcrumbs
          separator={<SeparatorIcon sx={{ fontSize: 16 }} />}
          aria-label="Setup breadcrumb"
          sx={{ '& .MuiBreadcrumbs-li': { display: 'flex', alignItems: 'center' } }}
        >
          <Link
            component={RouterLink}
            to={SETUP_ROOT}
            underline="hover"
            sx={{ fontSize: '0.8rem', fontWeight: 700, color: 'text.secondary' }}
          >
            Setup Master
          </Link>
          {currentGroup && (
            <Typography sx={{ fontSize: '0.8rem', fontWeight: 600, color: 'text.secondary' }}>
              {currentGroup.title}
            </Typography>
          )}
          {current && (
            <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }} aria-current="page">
              {setupEntryLabel(current, t)}
            </Typography>
          )}
        </Breadcrumbs>

        <Autocomplete<SetupEntry>
          size="small"
          options={options}
          // Selection is navigation, not state: the field clears itself and the page changes.
          value={null}
          blurOnSelect
          clearOnBlur
          disablePortal={false}
          groupBy={(option) => groupOfEntry(option.key)?.title ?? 'Setup'}
          getOptionLabel={(option) => setupEntryLabel(option, t)}
          isOptionEqualToValue={(option, value) => option.key === value.key}
          onChange={(_event, option) => {
            if (option) navigate(option.path);
          }}
          filterOptions={(all, state) => {
            const needle = state.inputValue.trim().toLowerCase();
            if (!needle) return all;
            return all.filter((option) =>
              [setupEntryLabel(option, t), option.label, option.description, ...(option.keywords ?? [])]
                .join(' ')
                .toLowerCase()
                .includes(needle),
            );
          }}
          renderOption={(props, option) => {
            const { key, ...optionProps } = props as React.HTMLAttributes<HTMLLIElement> & { key: string };
            return (
              <Box component="li" key={key} {...optionProps} sx={{ display: 'block !important', py: 1 }}>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {setupEntryLabel(option, t)}
                </Typography>
                <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'normal' }}>
                  {option.description}
                </Typography>
              </Box>
            );
          }}
          sx={{ width: { xs: '100%', sm: 300 } }}
          renderInput={(params) => (
            <TextField
              {...params}
              placeholder="Jump to a setting"
              slotProps={{
                // Spread the whole of `params.slotProps` first: it carries the refs Autocomplete
                // needs on both the wrapper and the html input. Replacing the object instead of
                // extending it costs the field its input ref, and MUI warns that it cannot find
                // the input element.
                ...params.slotProps,
                // The field has no visible label, and an `aria-label` on TextField lands on the
                // wrapping FormControl — not on the control that gets focus (SC 4.1.2). Name the
                // html input itself.
                htmlInput: { ...params.slotProps.htmlInput, 'aria-label': 'Jump to a setting' },
                input: {
                  ...params.slotProps.input,
                  startAdornment: <JumpIcon sx={{ fontSize: 18, color: 'text.secondary', ml: 0.5, mr: 0.5 }} />,
                },
              }}
              sx={{ '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
            />
          )}
        />
      </Box>

      <Outlet />
    </Box>
  );
};

/**
 * Auth gate for the whole Setup tree.
 *
 * `/setup` previously carried `PermissionGuard` on its CHILD routes only, so the index route — the
 * hub — rendered the full authenticated shell to anyone with no token, over copy telling them their
 * ROLE lacked access. That sent signed-out visitors to an administrator who would find nothing
 * wrong. Gating the shell rather than each child means every screen below it, including any added
 * later, inherits the redirect and none can be introduced without it.
 */
const SetupShell: React.FC = () => (
  <RequireAuth>
    <SetupShellChrome />
  </RequireAuth>
);

export default SetupShell;
