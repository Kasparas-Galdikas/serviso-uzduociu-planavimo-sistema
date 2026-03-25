function formatLocalDate(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function today(): string {
  return formatLocalDate(new Date());
}

export function addDays(dateStr: string, days: number): string {
  const [year, month, day] = dateStr.split("-").map(Number);
  const date = new Date(year, month - 1, day);
  date.setDate(date.getDate() + days);
  return formatLocalDate(date);
}

export function defaultPlanningDate(): string {
  return addDays(today(), 1);
}

export function compareLocalDates(left: string, right: string): number {
  return left.localeCompare(right);
}

export function isDateBefore(left: string, right: string): boolean {
  return compareLocalDates(left, right) < 0;
}

export function isDateTimeInPast(dateStr: string, minutes: number): boolean {
  const currentDate = today();
  if (isDateBefore(dateStr, currentDate)) {
    return true;
  }

  if (compareLocalDates(dateStr, currentDate) > 0) {
    return false;
  }

  const now = new Date();
  return minutes < now.getHours() * 60 + now.getMinutes();
}
