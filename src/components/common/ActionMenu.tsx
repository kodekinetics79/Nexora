import React from 'react';
import { Button, ListItemIcon, ListItemText, Menu, MenuItem } from '@mui/material';
import { KeyboardArrowDown as ChevronDownIcon } from '@mui/icons-material';

export interface ActionMenuItem {
  label: React.ReactNode;
  icon?: React.ReactNode;
  onClick: () => void;
  disabled?: boolean;
}

interface ActionMenuProps {
  label?: React.ReactNode;
  items: ActionMenuItem[];
  variant?: 'text' | 'outlined' | 'contained';
}

const ActionMenu: React.FC<ActionMenuProps> = ({ label = 'Actions', items, variant = 'contained' }) => {
  const [anchorEl, setAnchorEl] = React.useState<null | HTMLElement>(null);

  const close = () => setAnchorEl(null);

  return (
    <>
      <Button
        variant={variant}
        endIcon={<ChevronDownIcon />}
        onClick={(event) => setAnchorEl(event.currentTarget)}
        disabled={items.length === 0}
      >
        {label}
      </Button>
      <Menu anchorEl={anchorEl} open={Boolean(anchorEl)} onClose={close}>
        {items.map((item, index) => (
          <MenuItem
            key={index}
            disabled={item.disabled}
            onClick={() => {
              close();
              item.onClick();
            }}
            sx={{ borderRadius: 1.5, minWidth: 190 }}
          >
            {item.icon ? <ListItemIcon>{item.icon}</ListItemIcon> : null}
            <ListItemText primary={item.label} slotProps={{ primary: { variant: 'body2', sx: { fontWeight: 800 } } }} />
          </MenuItem>
        ))}
      </Menu>
    </>
  );
};

export default ActionMenu;
