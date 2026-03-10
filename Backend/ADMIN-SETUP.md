# Admin User Setup

This guide explains how to make users administrators in WhatsApp Bridge.

## Overview

WhatsApp Bridge supports an admin role system. Administrators have elevated privileges and can access admin-only features. By default, all users are created as regular users.

## Making a User Admin

Two scripts are provided to make users administrators:

### Option 1: PowerShell Script (Windows/Cross-platform)

```powershell
# Navigate to Backend directory
cd Backend

# Make a user admin
.\make-admin.ps1 -Email "user@example.com"

# With custom database path
.\make-admin.ps1 -Email "user@example.com" -DbPath "C:\path\to\whatsappbridge.db"
```

### Option 2: Bash Script (Linux/macOS)

```bash
# Navigate to Backend directory
cd Backend

# Make a user admin
./make-admin.sh user@example.com

# With custom database path
./make-admin.sh user@example.com /path/to/whatsappbridge.db
```

## Requirements

- **SQLite3** command-line tool must be installed on the server
  - Windows: Download from https://www.sqlite.org/download.html
  - Ubuntu/Debian: `sudo apt-get install sqlite3`
  - CentOS/RHEL: `sudo yum install sqlite`
  - macOS: Pre-installed

## Script Output

The script will:

1. Verify the user exists in the database
2. Show current admin status
3. Update the user to admin if not already
4. Confirm successful update

Example output:

```
WhatsApp Bridge - Make User Admin
=================================

Database: ../whatsappbridge.db
Email: user@example.com

Found user:
  ID: 1
  Email: user@example.com
  Current Admin Status: 0

SUCCESS: User 'user@example.com' is now an administrator!
```

## Verification

After running the script, the user will have admin privileges:

- JWT tokens will include `"role": "Admin"` claim
- API responses will show `"isAdmin": true`
- Frontend can check `user.isAdmin` to show/hide admin features

## Admin Features

Administrators have access to:

- All regular user features
- Future admin-only endpoints (e.g., user management, system settings)
- Admin dashboard (when implemented)

## Security Notes

- **Run scripts only on the server** where the database is located
- **Protect database file** with appropriate file permissions
- **Limit admin access** to trusted users only
- Scripts require direct database access (not available via API for security)

## Troubleshooting

### User not found

```
ERROR: User with email 'user@example.com' not found in database.
```

**Solution:** Verify the email address is correct. User must register first.

### Database file not found

```
ERROR: Database file not found at: ../whatsappbridge.db
```

**Solution:** Provide the correct path using `-DbPath` (PowerShell) or second argument (bash).

### SQLite not installed

```
ERROR: sqlite3 command not found. Please install SQLite.
```

**Solution:** Install SQLite3 command-line tool as described in Requirements section.

## Revoking Admin Access

To revoke admin access, run the same script logic but set `IsAdmin = 0`:

```bash
sqlite3 whatsappbridge.db "UPDATE Users SET IsAdmin = 0 WHERE Email = 'user@example.com';"
```

Or create a `revoke-admin.sh`/`revoke-admin.ps1` script with similar logic.
