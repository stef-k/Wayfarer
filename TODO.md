# TODO List

Each subtitle has a priority number with 1 being the first one to be implemented.

## Trip Edit view, priority 1

The dropdown controls where user select the icon should have a search field to be able for user to search them by name.

## Timeline API CRUD endpoints, priority 1

Mobile clients need to be able to edit/delete saved locations on server. So we need at least 2 endpoints:

a. Delete

b. Update where it can accept and update some or all of the following values: Coordinates, Notes, Activity and Local
Date Time.

## Trip API CRUD endpoints, priority 2

Mobile clients need to be able to add/edit/delete saved places to trips on server. So we need at least 3 endpoints:

a. Delete

b. Create a new place & Update an existing place some or all of the following values

b.1 Parent Region, if null then the Place will be added on the "Unassigned Places" region.

b.2 Name

b.3 Location

b.4 Notes

b.5 DisplayOrder

b.6 IconName

b.7 IconColor

The same should be also implement for the Regions (Parent Trip, Name, Center, Notes Cover Image Url).

## Trusted Managers Mechanism, priority 3

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
