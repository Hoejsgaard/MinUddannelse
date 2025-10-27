-- Migration: Add recurring reminder support to scheduled_tasks table
-- Date: 2025-10-27
-- Description: Adds task_type and reminder_id columns to support recurring reminders

-- Add task_type column with default 'hardcoded' for existing tasks
ALTER TABLE scheduled_tasks
ADD COLUMN task_type VARCHAR(50) DEFAULT 'hardcoded';

-- Add reminder_id column to link to template reminders
ALTER TABLE scheduled_tasks
ADD COLUMN reminder_id INTEGER REFERENCES reminders(id);

-- Add comments for documentation
COMMENT ON COLUMN scheduled_tasks.task_type IS 'Type of scheduled task: hardcoded (built-in) or reminder (user-created recurring reminder)';
COMMENT ON COLUMN scheduled_tasks.reminder_id IS 'Links to template reminder in reminders table (for task_type=reminder only)';

-- Update existing tasks to have explicit task_type
UPDATE scheduled_tasks SET task_type = 'hardcoded' WHERE task_type IS NULL;

-- Make task_type NOT NULL after setting defaults
ALTER TABLE scheduled_tasks
ALTER COLUMN task_type SET NOT NULL;

-- Create index for performance on task_type queries
CREATE INDEX idx_scheduled_tasks_task_type ON scheduled_tasks(task_type);

-- Verify migration
SELECT 'Migration completed successfully' AS status;