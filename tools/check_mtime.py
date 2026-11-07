import os
import datetime

stat = os.stat('src/Dvc.Web/appsettings.Live.json')
print("appsettings.Live.json mtime:", datetime.datetime.fromtimestamp(stat.st_mtime, datetime.timezone.utc).isoformat())
