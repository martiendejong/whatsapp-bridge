#!/bin/bash
#
# Make a user an administrator in WhatsApp Bridge
#
# Usage: ./make-admin.sh <email> [db-path]
# Example: ./make-admin.sh user@example.com
# Example: ./make-admin.sh user@example.com /path/to/whatsappbridge.db
#

set -e

EMAIL="$1"
DB_PATH="${2:-../whatsappbridge.db}"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;37m'
WHITE='\033[1;37m'
NC='\033[0m' # No Color

# Check if email was provided
if [ -z "$EMAIL" ]; then
    echo -e "${RED}ERROR: Email address is required${NC}"
    echo "Usage: $0 <email> [db-path]"
    exit 1
fi

# Check if sqlite3 is available
if ! command -v sqlite3 &> /dev/null; then
    echo -e "${RED}ERROR: sqlite3 command not found. Please install SQLite.${NC}"
    echo -e "${YELLOW}On Ubuntu/Debian: sudo apt-get install sqlite3${NC}"
    echo -e "${YELLOW}On CentOS/RHEL: sudo yum install sqlite${NC}"
    exit 1
fi

# Check if database exists
if [ ! -f "$DB_PATH" ]; then
    echo -e "${RED}ERROR: Database file not found at: $DB_PATH${NC}"
    exit 1
fi

echo -e "${CYAN}WhatsApp Bridge - Make User Admin${NC}"
echo -e "${CYAN}=================================${NC}"
echo ""
echo -e "${GRAY}Database: $DB_PATH${NC}"
echo -e "${GRAY}Email: $EMAIL${NC}"
echo ""

# Check if user exists
USER_INFO=$(sqlite3 "$DB_PATH" "SELECT Id, Email, IsAdmin FROM Users WHERE Email = '$EMAIL';")

if [ -z "$USER_INFO" ]; then
    echo -e "${RED}ERROR: User with email '$EMAIL' not found in database.${NC}"
    exit 1
fi

# Parse user info
IFS='|' read -r USER_ID USER_EMAIL IS_ADMIN <<< "$USER_INFO"

echo -e "${GREEN}Found user:${NC}"
echo -e "${WHITE}  ID: $USER_ID${NC}"
echo -e "${WHITE}  Email: $USER_EMAIL${NC}"
echo -e "${WHITE}  Current Admin Status: $IS_ADMIN${NC}"
echo ""

if [ "$IS_ADMIN" = "1" ]; then
    echo -e "${YELLOW}User is already an administrator.${NC}"
    exit 0
fi

# Update user to admin
sqlite3 "$DB_PATH" "UPDATE Users SET IsAdmin = 1 WHERE Email = '$EMAIL';"

# Verify update
NEW_STATUS=$(sqlite3 "$DB_PATH" "SELECT IsAdmin FROM Users WHERE Email = '$EMAIL';")

if [ "$NEW_STATUS" = "1" ]; then
    echo -e "${GREEN}SUCCESS: User '$EMAIL' is now an administrator!${NC}"
else
    echo -e "${RED}ERROR: Failed to update user admin status.${NC}"
    exit 1
fi
