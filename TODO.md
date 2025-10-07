# TODO List

Each subtitle has a priority number with 1 being the first one to be implemented.

## Chronologically Navigated Timeline, priority 1

Will show user's location date by specified date with default today's date.
The view will show data on map as with other timeline views, it will also contain controls 
that allow the user to pick a date, month or year. For selected periods of month or year, since they
will contain many locations ~8500 to ~21500, the frontend Leaflet map should be able to handle accordingly
by grouping locations.

In contrast with current private and public timeline implementations where we use sophisticated 
approach to filter locations on globe, this feature especially for one day selection,
will provide unfiltered locations based only on selected date.

This feature will be provided only as private view in user's area and as API endpoint in Api area so the mobile app
can access it by using user's API token.

## Trusted Managers Mechanism, priority 2

Implement a trusted user/manager mechanism to allow managers see user location data.

### Implementation List

* Add necessary database mechanism to link managers with users, possible a many to many table
* Create the UI in user's settings to add/delete trusted manager(s)
* Implement the manager interface to select a user and see his location data.

## Geofencing, priority 3

### Implementation List

* Create the UI in both User and Manager areas to create and store geofence areas
* Implement geofence queries in User and Manager areas

## Manager Fleet Tracking System, priority 4

### Implementation List

* Create the UI in manager's area to allow managers track vehicle location data
* Create the necessary database mechanism to link managers with vehicles, a possible many to many table
* Design the system so that a manager can only add/remove vehicles from his organization which leads to:
* Managers and Vehicles should have an additional DB field Organization as a unique identifying field linking them
